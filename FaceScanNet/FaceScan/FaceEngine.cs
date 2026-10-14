using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace FaceScan
{
    public class NoFaceException : Exception
    {
        public NoFaceException() : base("No face detected.")
        {
        }
    }

    /// <summary>
    /// Finds the largest face in a camera frame and turns it into a 128-number embedding.
    /// Detection: OpenCV's ResNet-10 SSD. Recognition: SFace, on a face aligned by its eyes.
    /// </summary>
    public sealed class FaceEngine : IDisposable
    {
        /// <summary>Stored with each enrollment, so a future model change cannot mix embeddings.</summary>
        public const string ModelName = "SFace-2021dec";

        private const int FaceSize = 112;
        private const float MinDetectionConfidence = 0.6f;

        // where SFace expects the eye centres in its 112x112 input (standard ArcFace template)
        private static readonly Point2f TemplateLeftEye = new Point2f(38.2946f, 51.6963f);
        private static readonly Point2f TemplateRightEye = new Point2f(73.5318f, 51.5014f);

        private readonly Net detector;
        private readonly Net recognizer;
        private readonly CascadeClassifier eyeDetector;
        private readonly object gate = new object();

        public FaceEngine(string modelsFolder)
        {
            detector = CvDnn.ReadNetFromCaffe(
                ModelFile(modelsFolder, "res10_300x300_ssd_deploy.prototxt"),
                ModelFile(modelsFolder, "res10_300x300_ssd_iter_140000.caffemodel"));
            recognizer = Net.ReadNetFromONNX(ModelFile(modelsFolder, "face_recognition_sface_2021dec.onnx"));
            eyeDetector = new CascadeClassifier(ModelFile(modelsFolder, "haarcascade_eye.xml"));
            if (detector.Empty() || recognizer.Empty() || eyeDetector.Empty())
            {
                throw new InvalidOperationException("Could not load the face models from " + modelsFolder);
            }
        }

        /// <summary>Embedding of the largest face in a BGR frame. Throws NoFaceException.</summary>
        public float[] Embed(Mat frame)
        {
            lock (gate)
            {
                var face = DetectLargestFace(frame);
                if (!face.HasValue)
                {
                    throw new NoFaceException();
                }
                using (var aligned = AlignFace(frame, face.Value))
                {
                    return Embedding(aligned);
                }
            }
        }

        public Rect? DetectLargestFace(Mat frame)
        {
            lock (gate)
            {
                using (var small = new Mat())
                {
                    Cv2.Resize(frame, small, new Size(300, 300));
                    using (var blob = CvDnn.BlobFromImage(small, 1.0, new Size(300, 300),
                        new Scalar(104, 177, 123), false, false))
                    {
                        detector.SetInput(blob);
                        using (var output = detector.Forward())
                        {
                            // output is 1 x 1 x N x 7: [image, class, confidence, x1, y1, x2, y2]
                            var count = output.Size(2);
                            var values = new float[count * 7];
                            Marshal.Copy(output.Data, values, 0, values.Length);

                            Rect? best = null;
                            for (var i = 0; i < count; i++)
                            {
                                if (values[i * 7 + 2] < MinDetectionConfidence)
                                {
                                    continue;
                                }
                                var x1 = Clamp(values[i * 7 + 3] * frame.Width, frame.Width);
                                var y1 = Clamp(values[i * 7 + 4] * frame.Height, frame.Height);
                                var x2 = Clamp(values[i * 7 + 5] * frame.Width, frame.Width);
                                var y2 = Clamp(values[i * 7 + 6] * frame.Height, frame.Height);
                                var rect = new Rect(x1, y1, x2 - x1, y2 - y1);
                                if (rect.Width < 20 || rect.Height < 20)
                                {
                                    continue;
                                }
                                if (!best.HasValue || rect.Width * rect.Height > best.Value.Width * best.Value.Height)
                                {
                                    best = rect;
                                }
                            }
                            return best;
                        }
                    }
                }
            }
        }

        /// <summary>112x112 face with the eyes moved to where SFace expects them.</summary>
        private Mat AlignFace(Mat frame, Rect face)
        {
            Mat transform;
            var eyes = FindEyes(frame, face);
            if (eyes != null)
            {
                transform = SimilarityTransform(eyes[0], eyes[1], TemplateLeftEye, TemplateRightEye);
            }
            else
            {
                // eyes not found (glasses glare, side light): fall back to a square crop of the face box
                var cx = face.X + face.Width / 2f;
                var cy = face.Y + face.Height / 2f;
                var half = Math.Max(face.Width, face.Height) / 2f;
                transform = Cv2.GetAffineTransform(
                    new[] { new Point2f(cx - half, cy - half), new Point2f(cx + half, cy - half), new Point2f(cx - half, cy + half) },
                    new[] { new Point2f(0, 0), new Point2f(FaceSize, 0), new Point2f(0, FaceSize) });
            }
            using (transform)
            {
                var aligned = new Mat();
                Cv2.WarpAffine(frame, aligned, transform, new Size(FaceSize, FaceSize));
                return aligned;
            }
        }

        /// <summary>Left and right eye centres in frame coordinates, or null if two eyes are not found.</summary>
        private Point2f[] FindEyes(Mat frame, Rect face)
        {
            using (var faceRegion = new Mat(frame, face))
            using (var gray = new Mat())
            {
                Cv2.CvtColor(faceRegion, gray, ColorConversionCodes.BGR2GRAY);
                var upperHeight = face.Height * 6 / 10;
                using (var upper = new Mat(gray, new Rect(0, 0, face.Width, upperHeight)))
                {
                    var minEye = Math.Max(1, face.Width / 10);
                    var found = eyeDetector.DetectMultiScale(upper, 1.1, 5, 0,
                        new Size(minEye, minEye));
                    if (found.Length < 2)
                    {
                        return null;
                    }
                    var centres = found
                        .OrderByDescending(e => e.Width * e.Height)
                        .Take(2)
                        .Select(e => new Point2f(face.X + e.X + e.Width / 2f, face.Y + e.Y + e.Height / 2f))
                        .OrderBy(p => p.X)
                        .ToArray();
                    if (centres[1].X - centres[0].X < face.Width * 0.2f)
                    {
                        return null;
                    }
                    return centres;
                }
            }
        }

        /// <summary>Rotation + uniform scale + translation mapping (p1, p2) onto (q1, q2).</summary>
        internal static Mat SimilarityTransform(Point2f p1, Point2f p2, Point2f q1, Point2f q2)
        {
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double du = q2.X - q1.X, dv = q2.Y - q1.Y;
            var lengthSquared = dx * dx + dy * dy;
            var a = (du * dx + dv * dy) / lengthSquared;
            var b = (dv * dx - du * dy) / lengthSquared;
            var tx = q1.X - (a * p1.X - b * p1.Y);
            var ty = q1.Y - (b * p1.X + a * p1.Y);

            var m = new Mat(2, 3, MatType.CV_64FC1);
            m.Set(0, 0, a);
            m.Set(0, 1, -b);
            m.Set(0, 2, tx);
            m.Set(1, 0, b);
            m.Set(1, 1, a);
            m.Set(1, 2, ty);
            return m;
        }

        private float[] Embedding(Mat face)
        {
            using (var blob = CvDnn.BlobFromImage(face, 1.0, new Size(FaceSize, FaceSize),
                new Scalar(0, 0, 0), true, false))
            {
                recognizer.SetInput(blob);
                using (var output = recognizer.Forward())
                {
                    var embedding = new float[output.Total()];
                    Marshal.Copy(output.Data, embedding, 0, embedding.Length);
                    var norm = Math.Sqrt(embedding.Sum(v => (double)v * v));
                    for (var i = 0; i < embedding.Length; i++)
                    {
                        embedding[i] = (float)(embedding[i] / norm);
                    }
                    return embedding;
                }
            }
        }

        private static int Clamp(float value, int max)
        {
            return Math.Max(0, Math.Min(max, (int)value));
        }

        private static string ModelFile(string folder, string name)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Face model file is missing: " + path, path);
            }
            return path;
        }

        public void Dispose()
        {
            detector.Dispose();
            recognizer.Dispose();
            eyeDetector.Dispose();
        }
    }
}
