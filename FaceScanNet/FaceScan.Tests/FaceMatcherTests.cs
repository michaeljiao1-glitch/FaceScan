using System.IO;
using NUnit.Framework;

namespace FaceScan.Tests
{
    [TestFixture]
    public class FaceMatcherTests
    {
        private static FaceRecord Face(string account, params float[] v)
        {
            return new FaceRecord(account, "m", v);
        }

        [Test]
        public void PicksClosestAccountAboveThreshold()
        {
            var enrolled = new[] { Face("1001", 1, 0, 0), Face("1002", 0, 1, 0) };
            var match = FaceMatcher.BestMatch(new float[] { 0.95f, 0.05f, 0 }, enrolled, 0.4, 0.05);
            Assert.AreEqual("1001", match.Account);
        }

        [Test]
        public void NobodyCloseEnough()
        {
            var enrolled = new[] { Face("1001", 1, 0, 0), Face("1002", 0, 1, 0) };
            Assert.IsNull(FaceMatcher.BestMatch(new float[] { 0, 0, 1 }, enrolled, 0.4, 0.05));
        }

        [Test]
        public void TooCloseToCallBetweenTwoPatients()
        {
            var enrolled = new[] { Face("1001", 1, 0, 0), Face("1002", 0.98f, 0.2f, 0) };
            Assert.IsNull(FaceMatcher.BestMatch(new float[] { 1, 0.1f, 0 }, enrolled, 0.4, 0.05));
        }

        [Test]
        public void SeveralEnrollmentsOfOnePatientDoNotCompeteWithEachOther()
        {
            var enrolled = new[] { Face("1001", 1, 0, 0), Face("1001", 0.98f, 0.2f, 0), Face("1002", 0, 1, 0) };
            Assert.AreEqual("1001", FaceMatcher.BestMatch(new float[] { 1, 0.1f, 0 }, enrolled, 0.4, 0.05).Account);
        }

        [Test]
        public void StoreSavesAndReloads()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "faces_test.dat");
            try
            {
                var store = new FaceStore(path);
                store.Add("1001", "SFace", new float[] { 0.5f, -0.25f, 1e-3f });
                store.Add(" 1002 ", "SFace", new float[] { 1, 2, 3 });
                store.Add("1003", "Other", new float[] { 1, 2, 3 });

                var reloaded = new FaceStore(path);
                var faces = reloaded.ForModel("SFace");
                Assert.AreEqual(2, faces.Count);
                Assert.AreEqual("1001", faces[0].Account);
                CollectionAssert.AreEqual(new float[] { 0.5f, -0.25f, 1e-3f }, faces[0].Embedding);
                Assert.AreEqual("1002", faces[1].Account);

                Assert.AreEqual(1, reloaded.Remove("1001"));
                Assert.AreEqual(1, new FaceStore(path).ForModel("SFace").Count);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path), true);
            }
        }
    }
}
