using System.IO;
using log4net;
using StreamIndex;

namespace Resin.IO.Read
{
    public class MappedTrieReader : TrieReader
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MappedTrieReader));

        private readonly Stream _stream;
        private readonly int _blockSize;
        private readonly bool _leaveOpen;

        public MappedTrieReader(string fileName)
        {
            var dir = Path.GetDirectoryName(fileName);
            var version = Path.GetFileNameWithoutExtension(fileName);

            _stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096*1, FileOptions.SequentialScan);

            _blockSize = Serializer.SizeOfNode() + BlockSerializer.SizeOfBlock();
        }

        public MappedTrieReader(Stream stream)
        {
            _stream = stream;
            _blockSize = Serializer.SizeOfNode() + BlockSerializer.SizeOfBlock();
            _leaveOpen = true;
        }

        protected override void Skip(int count)
        {
            if (count > 0)
            {
                _stream.Seek(_blockSize * count, SeekOrigin.Current);
            }
        }

        protected override LcrsNode Step()
        {
            if (Replay != LcrsNode.MinValue)
            {
                var replayed = Replay;
                Replay = LcrsNode.MinValue;
                return replayed;
            }

            var node = Serializer.DeserializeNode(_stream);

            LastRead = node;

            return LastRead;
        }

        public override void Dispose()
        {
            if (!_leaveOpen)
                _stream.Dispose();
        }
    }
}