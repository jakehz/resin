﻿using Microsoft.Extensions.Logging;
using Sir.Core;
using Sir.Document;
using Sir.KeyValue;
using Sir.VectorSpace;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sir.Search
{
    /// <summary>
    /// Dispatcher of sessions.
    /// </summary>
    public class SessionFactory : IDisposable, ISessionFactory
    {
        private ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, long>> _keys;
        private readonly ConcurrentDictionary<string, IList<(long offset, long length)>> _pageInfo;
        private ILogger<SessionFactory> _logger;

        public string Dir { get; }
        public IConfigurationProvider Config { get; }
        public IStringModel Model { get; }
        public ILoggerFactory LoggerFactory { get; }

        public SessionFactory(IConfigurationProvider config, IStringModel model, ILoggerFactory loggerFactory)
        {
            var time = Stopwatch.StartNew();

            Dir = config.Get("data_dir");
            Config = config;
            Model = model;

            if (!Directory.Exists(Dir))
            {
                Directory.CreateDirectory(Dir);
            }

            _pageInfo = new ConcurrentDictionary<string, IList<(long offset, long length)>>();
            _logger = loggerFactory.CreateLogger<SessionFactory>();
            LoggerFactory = loggerFactory;
            _keys = LoadKeys();

            _logger.LogInformation($"loaded keys in {time.Elapsed}");

            _logger.LogInformation($"sessionfactory is initiated.");
        }

        public ILogger<T> GetLogger<T>()
        {
            return LoggerFactory.CreateLogger<T>();
        }

        public long GetDocCount(string collection)
        {
            var fileName = Path.Combine(Dir, $"{collection.ToHash()}.dix");

            if (!File.Exists(fileName))
                return 0;

            return new FileInfo(fileName).Length / (sizeof(long) + sizeof(int));
        }

        public void Truncate(ulong collectionId)
        {
            var count = 0;

            foreach (var file in Directory.GetFiles(Dir, $"{collectionId}*"))
            {
                File.Delete(file);
                count++;
            }

            _pageInfo.Clear();

            _keys.Remove(collectionId, out _);

            _logger.LogInformation($"truncated collection {collectionId} ({count} files)");
        }

        public void TruncateIndex(ulong collectionId)
        {
            var count = 0;

            foreach (var file in Directory.GetFiles(Dir, $"{collectionId}*.ix"))
            {
                File.Delete(file);
                count++;
            }
            foreach (var file in Directory.GetFiles(Dir, $"{collectionId}*.ixp"))
            {
                File.Delete(file);
                count++;
            }
            foreach (var file in Directory.GetFiles(Dir, $"{collectionId}*.vec"))
            {
                File.Delete(file);
                count++;
            }
            foreach (var file in Directory.GetFiles(Dir, $"{collectionId}*.pos"))
            {
                File.Delete(file);
                count++;
            }

            _logger.LogInformation($"truncated index {collectionId} ({count} files)");

            _pageInfo.Clear();
        }

        public void SaveAs(
            ulong targetCollectionId, 
            IEnumerable<IDictionary<string, object>> documents,
            HashSet<string> indexFieldNames,
            HashSet<string> storeFieldNames,
            IStringModel model,
            int reportSize = 1000)
        {
            var job = new WriteJob(targetCollectionId, documents, model, storeFieldNames, indexFieldNames);

            Write(job, reportSize);
        }

        public void Write(WriteJob job, WriteSession writeSession, IndexSession indexSession, int reportSize = 1000)
        {
            _logger.LogInformation($"writing to collection {job.CollectionId}");

            var time = Stopwatch.StartNew();

            using (var indexer = new ProducerConsumerQueue<(long docId, IDictionary<string, object> document)>(1, document =>
            {
                Parallel.ForEach(document.document, kv =>
                //foreach (var kv in document.document)
                {
                    if (job.IndexedFieldNames.Contains(kv.Key) && kv.Value != null)
                    {
                        var keyId = writeSession.EnsureKeyExists(kv.Key);

                        indexSession.Put(document.docId, keyId, kv.Value.ToString());
                    }
                });
            }))
            {
                using (var writer = new ProducerConsumerQueue<IDictionary<string, object>>(1, document =>
                {
                    var docId = writeSession.Write(document, job.StoredFieldNames);

                    indexer.Enqueue((docId, document));
                }))
                {
                    var batchNo = 0;

                    foreach (var batch in job.Documents.Batch(reportSize))
                    {
                        var batchTime = Stopwatch.StartNew();

                        foreach (var document in batch)
                        {
                            writer.Enqueue(document);
                        }

                        var info = indexSession.GetIndexInfo();
                        var t = batchTime.Elapsed.TotalMilliseconds;
                        var docsPerSecond = (int)(reportSize / t * 1000);
                        var debug = string.Join('\n', info.Info.Select(x => x.ToString()));

                        _logger.LogInformation($"batch {++batchNo}\n{debug}\n{docsPerSecond} docs/s \n write queue {writer.Count}\n index queue {indexer.Count}");
                    }
                }
            }

            _logger.LogInformation($"processed write job (collection {job.CollectionId}), time in total: {time.Elapsed}");
        }

        public void Write(WriteJob job, int reportSize = 1000)
        {
            using (var writeSession = CreateWriteSession(job.CollectionId))
            using (var indexSession = CreateIndexSession(job.CollectionId))
            {
                Write(job, writeSession, indexSession, reportSize);
            }
        }

        public void Write(WriteJob job, IndexSession indexSession, int reportSize)
        {
            using (var writeSession = CreateWriteSession(job.CollectionId))
            {
                Write(job, writeSession, indexSession, reportSize);
            }
        }

        public void Write(
            IEnumerable<IDictionary<string, object>> documents, 
            IStringModel model, 
            HashSet<string> storedFieldNames,
            HashSet<string> indexedFieldNames,
            int reportSize = 1000
            )
        {
            foreach (var group in documents.GroupBy(d => (string)d[SystemFields.CollectionId]))
            {
                var collectionId = group.Key.ToHash();

                using (var writeSession = CreateWriteSession(collectionId))
                using (var indexSession = CreateIndexSession(collectionId))
                {
                    Write(
                        new WriteJob(
                            collectionId, 
                            group, 
                            model, 
                            storedFieldNames, 
                            indexedFieldNames), 
                        writeSession, 
                        indexSession,
                        reportSize);
                }
            }
        }

        public FileStream CreateLockFile(ulong collectionId)
        {
            return new FileStream(Path.Combine(Dir, collectionId + ".lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                   4096, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        }

        public void ClearPageInfo()
        {
            _pageInfo.Clear();
        }

        public IList<(long offset, long length)> GetAllPages(string pageFileName)
        {
            return _pageInfo.GetOrAdd(pageFileName, key =>
            {
                using (var ixpStream = CreateReadStream(key))
                {
                    return new PageIndexReader(ixpStream).GetAll();
                }
            });
        }

        public void Refresh()
        {
            _keys = LoadKeys();
        }

        public ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, long>> LoadKeys()
        {
            var timer = Stopwatch.StartNew();
            var allkeys = new ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, long>>();

            foreach (var keyFile in Directory.GetFiles(Dir, "*.kmap"))
            {
                var collectionId = ulong.Parse(Path.GetFileNameWithoutExtension(keyFile));
                ConcurrentDictionary<ulong, long> keys;

                if (!allkeys.TryGetValue(collectionId, out keys))
                {
                    keys = new ConcurrentDictionary<ulong, long>();
                    allkeys.GetOrAdd(collectionId, keys);
                }

                using (var stream = new FileStream(keyFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite))
                {
                    long i = 0;
                    var buf = new byte[sizeof(ulong)];
                    var read = stream.Read(buf, 0, buf.Length);

                    while (read > 0)
                    {
                        keys.GetOrAdd(BitConverter.ToUInt64(buf, 0), i++);

                        read = stream.Read(buf, 0, buf.Length);
                    }
                }
            }

            _logger.LogInformation($"loaded keyHash -> keyId mappings into memory for {allkeys.Count} collections in {timer.Elapsed}");

            return allkeys;
        }

        public void RegisterKeyMapping(ulong collectionId, ulong keyHash, long keyId)
        {
            var fileName = Path.Combine(Dir, string.Format("{0}.kmap", collectionId));
            ConcurrentDictionary<ulong, long> keys;

            if (!_keys.TryGetValue(collectionId, out keys))
            {
                keys = new ConcurrentDictionary<ulong, long>();
                _keys.GetOrAdd(collectionId, keys);
            }

            if (!keys.ContainsKey(keyHash))
            {
                keys.GetOrAdd(keyHash, keyId);

                using (var stream = CreateAppendStream(fileName))
                {
                    stream.Write(BitConverter.GetBytes(keyHash), 0, sizeof(ulong));
                }
            }
        }

        public long GetKeyId(ulong collectionId, ulong keyHash)
        {
            return _keys[collectionId][keyHash];
        }

        public bool TryGetKeyId(ulong collectionId, ulong keyHash, out long keyId)
        {
            var keys = _keys.GetOrAdd(collectionId, new ConcurrentDictionary<ulong, long>());

            if (!keys.TryGetValue(keyHash, out keyId))
            {
                keyId = -1;
                return false;
            }
            return true;
        }

        public string GetKey(ulong collectionId, long keyId)
        {
            using (var indexReader = new ValueIndexReader(CreateReadStream(Path.Combine(Dir, $"{collectionId}.kix"))))
            using (var reader = new ValueReader(CreateReadStream(Path.Combine(Dir, $"{collectionId}.key"))))
            {
                var keyInfo = indexReader.Get(keyId);

                return (string)reader.Get(keyInfo.offset, keyInfo.len, keyInfo.dataType);
            }
        }

        public DocumentStreamSession CreateDocumentStreamSession()
        {
            return new DocumentStreamSession(this);
        }

        public WriteSession CreateWriteSession(ulong collectionId)
        {
            var documentWriter = new DocumentWriter(collectionId, this);

            return new WriteSession(
                collectionId,
                documentWriter
            );
        }

        public IndexSession CreateIndexSession(ulong collectionId)
        {
            return new IndexSession(collectionId, this, Model, Config, LoggerFactory.CreateLogger<IndexSession>());
        }

        public IReadSession CreateReadSession()
        {
            return new ReadSession(
                this,
                Config,
                Model,
                new PostingsReader(this),
                LoggerFactory.CreateLogger<ReadSession>());
        }

        public Stream CreateAsyncReadStream(string fileName, int bufferSize = 4096)
        {
            return File.Exists(fileName)
            ? new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous)
            : null;
        }

        public Stream CreateReadStream(string fileName, int bufferSize = 4096, FileOptions fileOptions = FileOptions.RandomAccess)
        {
            return File.Exists(fileName)
                ? new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, fileOptions)
                : null;
        }

        public Stream CreateAsyncAppendStream(string fileName, int bufferSize = 4096)
        {
            return new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous);
        }

        public Stream CreateAppendStream(string fileName, int bufferSize = 4096)
        {
            if (!File.Exists(fileName))
            {
                using (var fs = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize))
                {
                }
            }

            return new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize);
        }

        public bool CollectionExists(ulong collectionId)
        {
            return File.Exists(Path.Combine(Dir, collectionId + ".vec"));
        }

        public bool CollectionIsIndexOnly(ulong collectionId)
        {
            if (!CollectionExists(collectionId))
                throw new InvalidOperationException($"{collectionId} dows not exist");

            return !File.Exists(Path.Combine(Dir, collectionId + ".docs"));
        }

        public void Dispose()
        {
        }
    }
}