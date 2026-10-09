using System;
using System.IO;
using System.Security.Cryptography;

namespace VRVlog.LilToonExporter
{
    // A transfer must use the last successfully saved output. In particular,
    // an overwritten path must not silently send another avatar's bytes.
    internal sealed class SavedExpressionVrm
    {
        internal string Path { get; }
        internal int Expressions { get; }
        internal int Poses { get; }
        readonly byte[] hash;
        internal SavedExpressionVrm(string path, byte[] bytes, int expressions, int poses)
        {
            Path = path; Expressions = expressions; Poses = poses;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
        }
        internal string VerifiedPath()
        {
            try
            {
                using (var input = File.OpenRead(Path))
                using (var sha = SHA256.Create())
                    if (System.Linq.Enumerable.SequenceEqual(hash, sha.ComputeHash(input))) return Path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw new InvalidOperationException("保存したVRMが移動・変更されています。もう一度VRMを保存してから送ってください。");
        }
    }
}
