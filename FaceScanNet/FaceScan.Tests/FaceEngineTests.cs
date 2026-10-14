using System;
using System.IO;
using NUnit.Framework;
using OpenCvSharp;

namespace FaceScan.Tests
{
    /// <summary>
    /// Recognition on DeepFace's sample photos (tests/unit/dataset in this repository).
    /// img1/img2/img11 are one person, img8/img9 another, img3 a third.
    /// </summary>
    [TestFixture]
    public class FaceEngineTests
    {
        private FaceEngine engine;
        private string dataset;

        [OneTimeSetUp]
        public void Load()
        {
            var bin = TestContext.CurrentContext.TestDirectory;
            dataset = Path.GetFullPath(Path.Combine(bin, @"..\..\..\..\tests\unit\dataset".Replace('\\', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(dataset))
            {
                Assert.Ignore("Sample photos not found at " + dataset);
            }
            engine = new FaceEngine(Path.Combine(bin, "models"));
        }

        [OneTimeTearDown]
        public void Unload()
        {
            if (engine != null)
            {
                engine.Dispose();
            }
        }

        private float[] Embed(string name)
        {
            using (var image = Cv2.ImRead(Path.Combine(dataset, name)))
            {
                Assert.IsFalse(image.Empty(), name);
                return engine.Embed(image);
            }
        }

        [Test]
        public void EmbeddingIsUnitLength128()
        {
            var embedding = Embed("img1.jpg");
            Assert.AreEqual(128, embedding.Length);
            Assert.AreEqual(1.0, FaceMatcher.CosineSimilarity(embedding, embedding), 1e-5);
        }

        [Test]
        public void RecognizesEnrolledPeopleAndRejectsStrangers()
        {
            var enrolled = new[]
            {
                new FaceRecord("1001", FaceEngine.ModelName, Embed("img1.jpg")),
                new FaceRecord("1002", FaceEngine.ModelName, Embed("img8.jpg")),
            };
            Func<string, string> identify = name =>
            {
                var match = FaceMatcher.BestMatch(Embed(name), enrolled, 0.40, 0.05);
                return match == null ? null : match.Account;
            };
            Assert.AreEqual("1001", identify("img2.jpg"));
            Assert.AreEqual("1001", identify("img11.jpg"));
            Assert.AreEqual("1002", identify("img9.jpg"));
            Assert.IsNull(identify("img3.jpg"));
        }

        [Test]
        public void BlankFrameHasNoFace()
        {
            using (var blank = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0)))
            {
                Assert.Throws<NoFaceException>(() => engine.Embed(blank));
            }
        }
    }
}
