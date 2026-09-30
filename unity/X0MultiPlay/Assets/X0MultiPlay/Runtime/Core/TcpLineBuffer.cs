using System.Collections.Generic;
using System.Text;

namespace X0.MultiPlay.Client
{
    // TCP 로 받은 거 줄 단위로 자르기. 잘려서 온 건 다음까지 들고 있음
    public sealed class TcpLineBuffer
    {
        const int MaxBytes = 1024 * 1024;
        readonly StringBuilder _acc = new StringBuilder();

        public bool Overflow { get; private set; }

        public IEnumerable<string> Push(byte[] data, int count)
        {
            if (Overflow || data == null || count <= 0)
                yield break;

            _acc.Append(Encoding.UTF8.GetString(data, 0, count));
            if (_acc.Length > MaxBytes)
            {
                Overflow = true;
                _acc.Clear();
                yield break;
            }

            while (true)
            {
                var s = _acc.ToString();
                var i = s.IndexOf('\n');
                if (i < 0)
                    yield break;
                var line = s.Substring(0, i).Trim();
                _acc.Remove(0, i + 1);
                if (line.Length > 0)
                    yield return line;
            }
        }
    }
}
