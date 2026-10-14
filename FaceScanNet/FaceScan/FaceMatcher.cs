using System;
using System.Collections.Generic;
using System.Linq;

namespace FaceScan
{
    public sealed class FaceMatch
    {
        public FaceMatch(string account, double similarity)
        {
            Account = account;
            Similarity = similarity;
        }

        public string Account { get; private set; }
        public double Similarity { get; private set; }
    }

    public static class FaceMatcher
    {
        public static double CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length)
            {
                throw new ArgumentException("Embeddings have different lengths.");
            }
            double dot = 0, normA = 0, normB = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }

        /// <summary>
        /// The enrolled account most similar to the probe face, or null when nobody reaches the
        /// threshold, or when a second account is within <paramref name="margin"/> of the best
        /// (too close to call - better to send the patient to the front desk than check in the
        /// wrong person).
        /// </summary>
        public static FaceMatch BestMatch(float[] probe, IEnumerable<FaceRecord> enrolled,
            double threshold, double margin)
        {
            var perAccount = enrolled
                .GroupBy(r => r.Account)
                .Select(g => new FaceMatch(g.Key, g.Max(r => CosineSimilarity(probe, r.Embedding))))
                .OrderByDescending(m => m.Similarity)
                .Take(2)
                .ToList();

            if (perAccount.Count == 0 || perAccount[0].Similarity < threshold)
            {
                return null;
            }
            if (perAccount.Count > 1 && perAccount[0].Similarity - perAccount[1].Similarity < margin)
            {
                return null;
            }
            return perAccount[0];
        }
    }
}
