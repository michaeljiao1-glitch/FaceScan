using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FaceScan
{
    public sealed class FaceRecord
    {
        public FaceRecord(string account, string model, float[] embedding)
        {
            Account = account;
            Model = model;
            Embedding = embedding;
        }

        public string Account { get; private set; }
        public string Model { get; private set; }
        public float[] Embedding { get; private set; }
    }

    /// <summary>
    /// Enrolled face embeddings for one clinic, kept in a local file (one line per enrollment).
    /// This is biometric data: keep the folder access-restricted.
    /// </summary>
    public sealed class FaceStore
    {
        private readonly string path;
        private readonly object gate = new object();
        private readonly List<FaceRecord> records = new List<FaceRecord>();

        public FaceStore(string path)
        {
            this.path = path;
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var record = Parse(line);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
            }
        }

        public void Add(string account, string model, float[] embedding)
        {
            var record = new FaceRecord(account.Trim(), model, embedding);
            lock (gate)
            {
                File.AppendAllText(path, Format(record) + Environment.NewLine, Encoding.UTF8);
                records.Add(record);
            }
        }

        public int Remove(string account)
        {
            lock (gate)
            {
                var removed = records.RemoveAll(r => r.Account == account.Trim());
                if (removed > 0)
                {
                    var temp = path + ".tmp";
                    File.WriteAllLines(temp, records.Select(Format), Encoding.UTF8);
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
                return removed;
            }
        }

        public IList<FaceRecord> ForModel(string model)
        {
            lock (gate)
            {
                return records.Where(r => r.Model == model).ToList();
            }
        }

        // account <TAB> model <TAB> base64 float32 embedding <TAB> enrolled time
        private static string Format(FaceRecord record)
        {
            var bytes = new byte[record.Embedding.Length * sizeof(float)];
            Buffer.BlockCopy(record.Embedding, 0, bytes, 0, bytes.Length);
            return string.Join("\t", record.Account, record.Model, Convert.ToBase64String(bytes),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
        }

        private static FaceRecord Parse(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                return null;
            }
            var bytes = Convert.FromBase64String(parts[2]);
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
            return new FaceRecord(parts[0], parts[1], embedding);
        }
    }
}
