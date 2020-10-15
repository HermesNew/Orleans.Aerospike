﻿using Aerospike.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Reminders.Aerospike
{
    public class AerospikeReminderTable : IReminderTable
    {
        private ClusterOptions _clusterOptions;
        private readonly IGrainReferenceConverter _grainReferenceConverter;
        private ILoggerFactory _loggerFactory;
        private ILogger<AerospikeReminderTable> _logger;
        private AerospikeReminderStorageOptions _options;
        private AsyncClient _client;
        private readonly string _serviceId;

        public AerospikeReminderTable(IGrainReferenceConverter grainReferenceConverter, ILoggerFactory loggerFactory, IOptions<ClusterOptions> clusterOptions, IOptions<AerospikeReminderStorageOptions> clusteringOptions)
        {
            _clusterOptions = clusterOptions.Value;
            _grainReferenceConverter = grainReferenceConverter;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory?.CreateLogger<AerospikeReminderTable>();
            _options = clusteringOptions.Value;
            _serviceId = string.IsNullOrWhiteSpace(clusterOptions.Value.ServiceId) ? Guid.Empty.ToString() : clusterOptions.Value.ServiceId;
            _serviceId = clusterOptions.Value.ServiceId;
        }

        public async Task Init()
        {
            _client = new AsyncClient(_options.Host, _options.Port);

            await Task.Run(() =>
            {
                try
                {
                    var task = _client.CreateIndex(new Policy(), _options.Namespace, _options.SetName + "_" + _serviceId, "grainhashIdx", "grainhash", IndexType.NUMERIC);
                    task.Wait();
                }
                catch (Exception)
                { }
            });
        }

        public Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            return Task.Run(() =>
            {
                var key = CreateKey(grainRef, reminderName);
                var record = _client.Get(new BatchPolicy() { sendKey = true }, key);
                if (record != null)
                    return ParseRecord(record);
                else
                    return null;
            });
        }

        public Task<ReminderTableData> ReadRows(GrainReference key)
        {
            return Task.Run(() =>
            {
                var entries = new List<ReminderEntry>();
                try
                {
                    var recordSet = _client.Query(new QueryPolicy { sendKey = true }, new Statement()
                    {
                        Filter = Filter.Equal("grainhash", key.GetUniformHashCode()),
                        Namespace = _options.Namespace,
                        SetName = _options.SetName,
                        IndexName = "grainhashIdx"
                    });

                    while (recordSet.Next())
                    {
                        entries.Add(ParseRecord(recordSet.Record));
                    }
                }
                catch (Exception exp)
                {
                    throw;
                }
                return new ReminderTableData(entries);
            });
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            return Task.Run(() =>
            {
                var entries = new List<ReminderEntry>();
                try
                {
                    //_client.Query(new QueryPolicy(), new Statement(), (a, r) => { });
                    var recordSet = _client.Query(new QueryPolicy { sendKey = true }, new Statement()
                    {
                        Filter = begin < end ? Filter.Range("grainhash", begin, end) : Filter.Range("grainhash", end, begin),
                        Namespace = _options.Namespace,
                        SetName = _options.SetName,
                        IndexName = "grainhashIdx"
                    });

                    while (recordSet.Next())
                    {
                        entries.Add(ParseRecord(recordSet.Record));
                    }
                }
                catch (Exception exp)
                {
                    throw;
                }
                return new ReminderTableData(entries);
            });
        }

        public Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            return Task.Run(() =>
            {
                var key = CreateKey(grainRef, reminderName);
                return _client.Delete(new WritePolicy(), key);
            });
        }

        public Task TestOnlyClearTable()
        {
            throw new NotImplementedException();
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            var key = CreateKey(entry.GrainRef, entry.ReminderName);
            var bins = ToBins(entry);

            if (string.IsNullOrEmpty(entry.ETag))
            {
                await _client.Put(new WritePolicy() { sendKey = true }, Task.Factory.CancellationToken, key, bins);
                return "1";
            }
            else
            {
                var gen = int.Parse(entry.ETag);
                await _client.Put(
                    new WritePolicy() { sendKey = true, generationPolicy = GenerationPolicy.EXPECT_GEN_EQUAL, generation = gen },
                    Task.Factory.CancellationToken,
                    key, bins);
                return (gen++).ToString();
            }
        }

        private Bin[] ToBins(ReminderEntry entry)
        {
            return new Bin[] 
            {
                //Id = ReminderEntity.ConstructId(entry.GrainRef, entry.ReminderName),
                //PartitionKey = ReminderEntity.ConstructPartitionKey(this._serviceId, entry.GrainRef),
                new Bin("serviceid", _serviceId),
                new Bin("grainhash", entry.GrainRef.GetUniformHashCode()),
                new Bin("grainref", entry.GrainRef.ToKeyString()),
                new Bin("name", entry.ReminderName),
                new Bin("startat",  entry.StartAt.ToBinary()),
                new Bin("period", entry.Period.Ticks)
            };
        }
        private ReminderEntry ParseRecord(Record record)
        {
            var entry = new ReminderEntry();
            entry.ETag = record.generation.ToString();
            entry.GrainRef = _grainReferenceConverter.GetGrainFromKeyString((string)record.bins["grainref"]);
            entry.Period = new TimeSpan((long)record.bins["period"]);
            entry.ReminderName = (string)record.bins["name"];
            entry.StartAt = DateTime.FromBinary((long)record.bins["startat"]);
            return entry;
        }

        private string GetId(GrainReference grainRef, string reminderName)
        {
            return grainRef.ToShortKeyString() + reminderName;
        }

        private Key CreateKey(GrainReference grainRef, string reminderName)
        {
            return new Key(_options.Namespace, _options.SetName + (string.IsNullOrWhiteSpace(_serviceId) ? "" : "_" + _serviceId), GetId(grainRef, reminderName));
        }
    }
}
