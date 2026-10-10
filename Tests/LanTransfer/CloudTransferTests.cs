using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace VRVlog.LilToonExporter.LanTransfer.Tests
{
    public sealed class CloudTransferTests
    {
        [Test]
        public void CloudDevelopmentAvailabilityRequiresBothEditorAndExplicitDevelopmentDefine()
        {
#if UNITY_EDITOR && VRVLOG_CLOUD_TRANSFER_DEVELOPMENT
            Assert.That(CloudTransferAvailability.Enabled, Is.True);
#elif UNITY_EDITOR
            var version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CloudTransferAvailability).Assembly)?.version;
            Assert.That(CloudTransferAvailability.Enabled, Is.EqualTo(CloudTransferAvailability.IsSupportedVersion(version)));
#else
            Assert.That(CloudTransferAvailability.Enabled, Is.False);
#endif
            if (CloudTransferAvailability.Enabled) CloudTransferAvailability.RequireEnabled();
            else Assert.Throws<NotSupportedException>(() => CloudTransferAvailability.RequireEnabled());
            Assert.That(LanTransferAvailability.Enabled, Is.False);
        }

        [TestCase("0.11.12", false)]
        [TestCase("0.11.12+beta.1", false)]
        [TestCase("0.11.12-beta.1", true)]
        [TestCase("0.11.13-rc.1+build.2", true)]
        [TestCase("not-a-beta", false)]
        [TestCase(null, false)]
        public void CloudReleaseChannelRequiresAnActualPrerelease(string version, bool enabled) =>
            Assert.That(CloudTransferAvailability.IsPrereleaseVersion(version), Is.EqualTo(enabled));

        [TestCase("0.11.14", false)]
        [TestCase("0.11.15", true)]
        [TestCase("0.11.15-beta.2", true)]
        [TestCase("0.11.15+beta.2", false)]
        [TestCase("0.11.16", false)]
        [TestCase("not-a-beta", false)]
        [TestCase(null, false)]
        public void CloudTransferSupportsTheAdoptedStableVersion(string version, bool enabled) =>
            Assert.That(CloudTransferAvailability.IsSupportedVersion(version), Is.EqualTo(enabled));

        [TestCase(false)][TestCase(true)]
        public void DevelopmentCopyPreservesTheSavedVrmAndDeletesOnlyItsOwnedCopy(bool adopt)
        {
            var original = Path.Combine(Path.GetTempPath(), "vrvlog-saved-copy-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = Enumerable.Range(0, 65537).Select(index => (byte)(index * 37 + 11)).ToArray();
            string snapshotPath = null;
            File.WriteAllBytes(original, bytes);
            try
            {
                string before;
                using (var hash = SHA256.Create()) before = LanTransferProtocol.Hex(hash.ComputeHash(File.ReadAllBytes(original)));
                using (var snapshot = CloudDevelopmentSnapshot.CopySavedVrm(original))
                {
                    snapshotPath = snapshot.SnapshotPath;
                    Assert.That(snapshotPath == original, Is.False);
                    Assert.That(snapshot.Name, Is.EqualTo(Path.GetFileName(original)));
                    Assert.That(File.ReadAllBytes(snapshotPath), Is.EqualTo(bytes));
                    if (adopt)
                    {
                        using (var source = new CloudVrmTransferSource(snapshotPath, snapshot.Name))
                        {
                            snapshot.ReleaseOwnership();
                            snapshot.Dispose();
                            Assert.That(File.Exists(snapshotPath), Is.True);
                            Assert.That(source.FileHash, Is.EqualTo(before));
                        }
                    }
                }
                Assert.That(File.Exists(snapshotPath), Is.False);
                Assert.That(File.ReadAllBytes(original), Is.EqualTo(bytes));
                using (var hash = SHA256.Create()) Assert.That(LanTransferProtocol.Hex(hash.ComputeHash(File.ReadAllBytes(original))), Is.EqualTo(before));
            }
            finally { if (snapshotPath != null) LanVrmTransferServer.TryDelete(snapshotPath); File.Delete(original); }
        }

        [TestCase("short")][TestCase("long")][TestCase("io")]
        public void InterruptedDevelopmentCopyDeletesPartialDataWithoutChangingTheSavedVrm(string fault)
        {
            var original = Path.Combine(Path.GetTempPath(), "vrvlog-saved-failure-" + Guid.NewGuid().ToString("N") + ".vrm");
            var bytes = new byte[65537]; bytes[65536] = 17;
            string partial = null;
            File.WriteAllBytes(original, bytes);
            try
            {
                var declared = bytes.Length + (fault == "short" ? 1 : fault == "long" ? -1 : 0);
                var input = new DevelopmentCopyInput(bytes, declared, fault == "io");
                Assert.Throws<IOException>(() => CloudDevelopmentSnapshot.CopySavedVrm(original, () => input, path => partial = path));
                Assert.That(input.Disposed, Is.True);
                Assert.That(partial != null, Is.True);
                Assert.That(File.Exists(partial), Is.False);
                Assert.That(File.ReadAllBytes(original), Is.EqualTo(bytes));
            }
            finally { File.Delete(original); }
        }

        [TestCase(0L)][TestCase(268435457L)]
        public void DevelopmentCopyRejectsInvalidSizeBeforeCreatingOrReadingPayload(long declared)
        {
            var input = new DevelopmentCopyInput(new byte[] { 1 }, declared);
            var created = false;
            Assert.Throws<InvalidOperationException>(() => CloudDevelopmentSnapshot.CopySavedVrm("synthetic.vrm", () => input, path => created = true));
            Assert.That(input.ReadCalls, Is.EqualTo(0));
            Assert.That(input.Disposed, Is.True);
            Assert.That(created, Is.False);
        }

        [Test]
        public void DevelopmentCopyReadsInBoundedChunksAndRejectsNonVrmSelections()
        {
            var bytes = new byte[1048577]; bytes[1048576] = 29;
            var input = new DevelopmentCopyInput(bytes, bytes.Length);
            using (var snapshot = CloudDevelopmentSnapshot.CopySavedVrm("synthetic.VRM", () => input))
                Assert.That(File.ReadAllBytes(snapshot.SnapshotPath), Is.EqualTo(bytes));
            Assert.That(input.LargestRead, Is.LessThanOrEqualTo(64 * 1024));
            Assert.That(input.Disposed, Is.True);
            var opened = false;
            Assert.Throws<InvalidOperationException>(() => CloudDevelopmentSnapshot.CopySavedVrm("synthetic.zip", () => { opened = true; return new MemoryStream(bytes); }));
            Assert.That(opened, Is.False);
        }

        private sealed class DevelopmentCopyInput : MemoryStream
        {
            private readonly long declared;
            private readonly bool failAfterFirstRead;
            internal int LargestRead, ReadCalls;
            internal bool Disposed;
            internal DevelopmentCopyInput(byte[] bytes, long declared, bool failAfterFirstRead = false) : base(bytes)
            { this.declared = declared; this.failAfterFirstRead = failAfterFirstRead; }
            public override long Length => declared;
            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                if (failAfterFirstRead && ReadCalls > 1) throw new IOException("synthetic interrupted read");
                LargestRead = Math.Max(LargestRead, count);
                return base.Read(buffer, offset, count);
            }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        [Test]
        public async Task MultipartUploadPreservesBytesAndPublishesQrOnlyAfterSuccess()
        {
            using (var fixture = new Fixture(CloudTransferProtocol.PartSize + 1))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.BeforePublish = () => Assert.That(session.Qr == null, Is.True);
                await session.UploadAsync();
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Ready));
                Assert.That(DecryptUploaded(fixture.Transport, session.Qr), Is.EqualTo(fixture.Bytes));
                Assert.That(fixture.Transport.Parts.Select(part => part.Length).ToArray(), Is.EqualTo(new[] { CloudTransferProtocol.PartSize, 72 }));
                Assert.That(session.Transferred, Is.EqualTo(CloudEncryptedSnapshot.WireSize(fixture.Source.Size)));
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                fields.Exact("v", "id", "token", "name", "size", "sha256", "expiresAt", "key");
                Assert.That(fields.Number("v"), Is.EqualTo(3L));
                Assert.That(fields.Text("name"), Is.EqualTo("テストアバター.vrm"));
                Assert.That(fields.Text("sha256"), Is.EqualTo(fixture.Source.FileHash));
                Assert.That(fields.Text("token"), Is.EqualTo(fixture.Transport.ReadToken));
                Assert.That(session.Qr.Contains(fixture.Transport.UploadToken), Is.False);
                Assert.That(session.Qr.Contains("host") || session.Qr.Contains("https://"), Is.False);
                Assert.That(fixture.Transport.AuthenticatedCorrectly, Is.True);
                Assert.That(CloudTransferProtocol.IsKey(fields.Text("key")), Is.True);
                Assert.That(fixture.Transport.Metadata.Contains(fixture.Source.Name) || fixture.Transport.Metadata.Contains(fixture.Source.FileHash) || fixture.Transport.Metadata.Contains(fields.Text("key")), Is.False);
                Assert.That(fixture.Transport.RequestTokens.Contains(fields.Text("key")), Is.False);
            }
        }

        [Test]
        public async Task CloudNameSanitizationKeepsTheCompletedVrmBytesAndHash()
        {
            using (var fixture = new Fixture(47, "衣装/夜\\昼\u0000\u202e.vrm"))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                Assert.That(fixture.Source.Name, Is.EqualTo("衣装-夜-昼.vrm"));
                using (var hash = SHA256.Create())
                    Assert.That(fixture.Source.FileHash, Is.EqualTo(LanTransferProtocol.Hex(hash.ComputeHash(fixture.Bytes))));
                await session.UploadAsync();
                Assert.That(DecryptUploaded(fixture.Transport, session.Qr), Is.EqualTo(fixture.Bytes));
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                Assert.That(fields.Text("name"), Is.EqualTo("衣装-夜-昼.vrm"));
                Assert.That(fields.Text("sha256"), Is.EqualTo(fixture.Source.FileHash));
            }
        }

        [Test]
        public void MaximumUnicodeCloudNameMatchesTheWorkerUtf16LengthBound()
        {
            var name = string.Concat(Enumerable.Repeat("\ud83d\ude00", 128)) + "/\\suffix";
            var display = CloudTransferProtocol.DisplayName(name);
            Assert.That(display.Length, Is.EqualTo(256));
            Assert.That(display, Is.EqualTo(name.Substring(0, 256)));
            var qr = CloudTransferProtocol.Qr(new string('0', 32), new string('A', 43), display, CloudTransferProtocol.MaximumSize, new string('0', 64), 2147483647, new string('A', 86));
            Assert.That(Encoding.UTF8.GetByteCount(qr), Is.LessThanOrEqualTo(4096));
        }

        [TestCase("part-failure")][TestCase("publish-mismatch")][TestCase("redirect")][TestCase("quota")][TestCase("legacy-create")][TestCase("legacy-publish")]
        public async Task UploadFailuresNeverExposeQrAndCleanCreatedTransfer(string failure)
        {
            using (var fixture = new Fixture(17))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.Failure = failure;
                try { await session.UploadAsync(); Assert.That(false, Is.True); }
                catch (InvalidOperationException exception) { Assert.That(exception.Message.Contains(fixture.Transport.UploadToken), Is.False); }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Failed));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(File.Exists(fixture.Path), Is.True);
                Assert.That(session.HasEncryptionKey, Is.False);
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(failure == "quota" || failure == "redirect" ? 0 : 1));
            }
        }

        [TestCase("completed")][TestCase("cancelled")][TestCase("expired")][TestCase("failed")]
        public async Task TerminalStatusInvalidatesQrAndAllowsFreshSessionWithSameBytes(string remote)
        {
            using (var fixture = new Fixture(19))
            {
                string oldQr; byte[] oldEnvelope;
                using (var first = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
                {
                    await first.UploadAsync(); oldQr = first.Qr; oldEnvelope = fixture.Transport.Parts.SelectMany(part => part).ToArray();
                    fixture.Transport.RemoteState = remote;
                    await first.RefreshAsync();
                    Assert.That(first.Terminal, Is.True); Assert.That(first.Qr == null, Is.True);
                    Assert.That(first.HasEncryptionKey, Is.False);
                    Assert.That(File.Exists(fixture.Path), Is.True);
                }
                fixture.Transport.RemoteState = "ready";
                fixture.Transport.Parts.Clear();
                using (var second = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
                {
                    await second.UploadAsync();
                    Assert.That(second.Qr != oldQr, Is.True);
                    var oldFields = CloudTransferProtocol.Fields.Read(oldQr.Substring(LanTransferProtocol.QrPrefix.Length));
                    var fields = CloudTransferProtocol.Fields.Read(second.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                    Assert.That(fields.Text("key") == oldFields.Text("key"), Is.False);
                    Assert.That(fixture.Transport.Parts.SelectMany(part => part).Skip(8).Take(16).SequenceEqual(oldEnvelope.Skip(8).Take(16)), Is.False);
                    Assert.That(DecryptUploaded(fixture.Transport, second.Qr), Is.EqualTo(fixture.Bytes));
                }
            }
        }

        [Test]
        public async Task CancellationDuringCreateStillDeletesSessionWhenCredentialsArrive()
        {
            using (var fixture = new Fixture(23))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.CreateRelease = new TaskCompletionSource<bool>();
                var upload = session.UploadAsync();
                await fixture.Transport.CreateObserved.Task;
                await session.CancelAsync();
                fixture.Transport.CreateRelease.SetResult(true);
                try { await upload; Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Canceled));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(fixture.Transport.Parts.Count, Is.EqualTo(0));
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(1));
                Assert.That(session.HasEncryptionKey, Is.False);
            }
        }

        [Test]
        public async Task FailedStatusPollKeepsReadySessionForRetry()
        {
            using (var fixture = new Fixture(7))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                await session.UploadAsync(); var qr = session.Qr;
                fixture.Transport.Failure = "poll-failure";
                try { await session.RefreshAsync(); Assert.That(false, Is.True); } catch (InvalidDataException) { }
                Assert.That(session.Qr, Is.EqualTo(qr)); Assert.That(session.State, Is.EqualTo(CloudTransferState.Ready));
                fixture.Transport.Failure = null; fixture.Transport.RemoteState = "completed";
                await session.RefreshAsync(); Assert.That(session.State, Is.EqualTo(CloudTransferState.Completed));
            }
        }

        [Test]
        public async Task DeadlineExpiresIndependentlyOfAnUnfinishedStatusRequest()
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var fixture = new Fixture(11))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                await session.UploadAsync();
                fixture.Transport.StatusRelease = new TaskCompletionSource<bool>();
                var polling = session.RefreshAsync();
                Assert.That(polling.IsCompleted, Is.False);
                currentTime = session.ExpiresAt;
                Assert.That(session.ExpireIfDue(), Is.True);
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Expired));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(session.HasEncryptionKey, Is.False);
                Assert.That(fixture.Transport.StatusCancellationObserved, Is.True);
                try { await polling; Assert.That(false, Is.True); } catch (OperationCanceledException) { }
                Assert.That(session.ExpireIfDue(), Is.False);
            }
        }

        [TestCase(180)][TestCase(120)]
        public async Task UploadKeepsTheServerDeadlineWithoutRestartingItWhenQrAppears(int serverLifetimeSeconds)
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var currentTime = createdAt;
            using (var fixture = new Fixture(23))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                fixture.Transport.Now = () => createdAt;
                fixture.Transport.LifetimeSeconds = serverLifetimeSeconds;
                fixture.Transport.BeforePart = () => currentTime += 60;
                await session.UploadAsync();
                var expectedDeadline = createdAt + serverLifetimeSeconds;
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                Assert.That(session.ExpiresAt, Is.EqualTo(expectedDeadline));
                Assert.That(fields.Number("expiresAt"), Is.EqualTo(expectedDeadline));
                Assert.That(session.ExpiresAt - currentTime, Is.EqualTo((long)serverLifetimeSeconds - 60));
                currentTime = expectedDeadline - 1;
                Assert.That(session.ExpireIfDue(), Is.False);
                currentTime = expectedDeadline;
                Assert.That(session.ExpireIfDue(), Is.True);
                Assert.That(session.Qr == null, Is.True);
            }
        }

        [Test]
        public async Task UploadPastTheThreeMinuteDeadlineStopsBeforeTheNextPartOrPublication()
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var fixture = new Fixture(CloudTransferProtocol.PartSize + 1))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                fixture.Transport.Now = () => currentTime;
                fixture.Transport.BeforePart = () => currentTime = session.ExpiresAt;
                try { await session.UploadAsync(); Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Expired));
                Assert.That(fixture.Transport.Parts.Count, Is.EqualTo(1));
                Assert.That(fixture.Transport.PublishCalls, Is.EqualTo(0));
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(1));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
            }
        }

        [TestCase(900)][TestCase(0)]
        public async Task RejectedCreateLifetimeCancelsTheReservedTransferWithoutUploading(int rejectedLifetimeSeconds)
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var fixture = new Fixture(29))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                fixture.Transport.Now = () => currentTime;
                fixture.Transport.LifetimeSeconds = rejectedLifetimeSeconds;
                try { await session.UploadAsync(); Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Failed));
                Assert.That(fixture.Transport.Parts.Count, Is.EqualTo(0));
                Assert.That(fixture.Transport.PublishCalls, Is.EqualTo(0));
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(1));
                Assert.That(fixture.Transport.AuthenticatedCorrectly, Is.True);
                Assert.That(session.Qr == null, Is.True);
            }
        }

        [Test]
        public async Task PublicationResponsePastDeadlineCannotExposeAnExpiredQr()
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var fixture = new Fixture(13))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                fixture.Transport.BeforePublish = () => currentTime = session.ExpiresAt;
                try { await session.UploadAsync(); Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Expired));
                Assert.That(session.Qr == null, Is.True);
                Assert.That(fixture.Transport.CancelCalls, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ConfirmedPhoneSaveKeepsItsOutcomeAfterTheDeadlineWithoutClaimingCloudDeletion()
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var fixture = new Fixture(15))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport, () => currentTime))
            {
                await session.UploadAsync();
                fixture.Transport.RemoteState = "completed";
                await session.RefreshAsync();
                currentTime = session.ExpiresAt + 1;
                Assert.That(session.ExpireIfDue(), Is.False);
                Assert.That(session.State, Is.EqualTo(CloudTransferState.Completed));
                Assert.That(session.Message.Contains("削除対象"), Is.True);
                Assert.That(session.Message.Contains("削除しました"), Is.False);
            }
        }

        [Test]
        public void FlatParserRejectsAmbiguousAndUnboundedServiceMetadata()
        {
            foreach (var body in new[] { "{\"v\":2,\"v\":2}", "{\"size\":1.0}", "{\"size\":01}", "{\"id\":{}}", "{\"id\":[]}", "{\"id\":true}", "{\"id\":\"x\",}", "{}tail", new string(' ', 4097) })
                Assert.Throws<InvalidDataException>(() => CloudTransferProtocol.Fields.Read(body));
            Assert.That(CloudTransferProtocol.IsToken(new string('A', 42) + "B"), Is.False);
            Assert.That(CloudTransferProtocol.IsToken(new string('A', 43)), Is.True);
            Assert.That(CloudTransferProtocol.IsKey(new string('A', 86)), Is.True);
            Assert.That(CloudTransferProtocol.IsKey(new string('A', 85) + "B"), Is.False);
            Assert.That(CloudTransferProtocol.IsKey(new string('A', 43)), Is.False);
        }

        [Test]
        public void SourceDisposalDeletesOnlyItsOwnedTransferSnapshot()
        {
            var fixture = new Fixture(9); var path = fixture.Path;
            var ordinary = path + ".ordinary.vrm"; File.WriteAllBytes(ordinary, fixture.Bytes);
            try
            {
                fixture.Dispose();
                Assert.That(File.Exists(path), Is.False); Assert.That(File.ReadAllBytes(ordinary), Is.EqualTo(fixture.Bytes));
            }
            finally { File.Delete(ordinary); }
        }

        [Test]
        public void ClosingSourceReleasesReadersEvenBeforeSessionRegistersThem()
        {
            var fixture = new Fixture(31);
            var reader = fixture.Source.OpenRead();
            fixture.Dispose();
            Assert.That(reader.CanRead, Is.False);
            Assert.That(File.Exists(fixture.Path), Is.False);
            Assert.Throws<ObjectDisposedException>(() => fixture.Source.OpenRead());
        }

        [TestCase(0L)][TestCase(268435457L)]
        public void InvalidSnapshotSizesAreRejectedAndOwnedFileIsCleaned(long size)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrvlog-cloud-size-" + Guid.NewGuid().ToString("N") + ".vrm");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(size);
            Assert.Throws<InvalidOperationException>(() => new CloudVrmTransferSource(path, "fixture.vrm"));
            Assert.That(File.Exists(path), Is.False);
        }

        [TestCase(1L, 72L)][TestCase(15L, 72L)][TestCase(16L, 88L)][TestCase(17L, 88L)][TestCase(268435456L, 268435528L)]
        public void CipherWireSizeIncludesPkcs7AndAuthenticationEnvelope(long plainSize, long wireSize)
        { Assert.That(CloudEncryptedSnapshot.WireSize(plainSize), Is.EqualTo(wireSize)); }

        [TestCase("key")][TestCase("iv")][TestCase("ciphertext")][TestCase("tag")][TestCase("name")][TestCase("hash")]
        public async Task UploadedEnvelopeRejectsWrongKeyOrTamperedAuthenticatedData(string target)
        {
            using (var fixture = new Fixture(73))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                await session.UploadAsync();
                var fields = CloudTransferProtocol.Fields.Read(session.Qr.Substring(LanTransferProtocol.QrPrefix.Length));
                var envelope = fixture.Transport.Parts.SelectMany(part => part).ToArray();
                var key = DecodeKey(fields.Text("key")); var name = fields.Text("name"); var hash = fields.Text("sha256");
                if (target == "key") key[0] ^= 1;
                if (target == "iv") envelope[8] ^= 1;
                if (target == "ciphertext") envelope[24] ^= 1;
                if (target == "tag") envelope[envelope.Length - 1] ^= 1;
                if (target == "name") name += "x";
                if (target == "hash") hash = new string('0', 64);
                Assert.Throws<CryptographicException>(() => AuthenticatedDecrypt(envelope, key, name, fixture.Source.Size, hash));
            }
        }

        [Test]
        public async Task CancellationBeforeEncryptionNeverContactsTheCloudOrRetainsAKey()
        {
            using (var fixture = new Fixture(31))
            using (var cancellation = new CancellationTokenSource())
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                cancellation.Cancel();
                try { await session.UploadAsync(cancellation.Token); Assert.That(false, Is.True); } catch (InvalidOperationException) { }
                Assert.That(fixture.Transport.Created, Is.EqualTo(0));
                Assert.That(fixture.Transport.Parts.Count, Is.EqualTo(0));
                Assert.That(session.HasEncryptionKey, Is.False);
                Assert.That(session.Qr == null, Is.True);
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
            }
        }

        [Test]
        public async Task EncryptedSnapshotSupportsConcurrentReadersAndWindowsWriterExclusion()
        {
            using (var fixture = new Fixture(101))
            using (var encrypted = await CloudEncryptedSnapshot.CreateAsync(fixture.Source, CancellationToken.None))
            using (var first = encrypted.OpenRead())
            using (var second = encrypted.OpenRead())
            {
                Assert.That(first.Length, Is.EqualTo(encrypted.Size));
                Assert.That(first.ReadByte(), Is.EqualTo(second.ReadByte()));
                using (var digest = SHA256.Create())
                { second.Position = 0; Assert.That(LanTransferProtocol.Hex(digest.ComputeHash(second)), Is.EqualTo(encrypted.FileHash)); }
                // .NET on Unix does not enforce Windows FileShare exclusions.
                // Keep the required writer exclusion assertion on Windows CI.
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    Assert.Throws<IOException>(() => { using (var writer = new FileStream(encrypted.SnapshotPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } });
            }
        }

        [TestCase("quota", "HTTP 429", "転送の作成")]
        [TestCase("part-failure", "HTTP 500", "アップロード")]
        [TestCase("io-failure", "一時ファイル", "転送の作成")]
        [TestCase("network-failure", "接続", "転送の作成")]
        public async Task SafeFailureMessagesIdentifyCauseAndStageWithoutPrivateDetails(string kind, string reason, string stage)
        {
            using (var fixture = new Fixture(17))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.Failure = kind;
                try { await session.UploadAsync(); Assert.That(false, Is.True, "Must fail"); }
                catch (InvalidOperationException exception) { Assert.That(exception.Message, Is.EqualTo(session.Message)); }
                Assert.That(session.Message.Contains(reason) && session.Message.Contains(stage), Is.True);
                Assert.That(new[] { "SECRET", fixture.Path, fixture.Transport.UploadToken, "private service failure" }.Any(value => session.Message.Contains(value)), Is.False);
                Assert.That(session.Qr == null, Is.True);
                Assert.That(session.HasEncryptionKey, Is.False);
            }
        }

#if UNITY_EDITOR && !VRVLOG_LAN_TRANSFER_CLI
        [TestCase("en", "quota")][TestCase("en", "part-failure")][TestCase("en", "io-failure")][TestCase("en", "network-failure")]
        [TestCase("ko", "quota")][TestCase("ko", "part-failure")][TestCase("ko", "io-failure")][TestCase("ko", "network-failure")]
        [TestCase("zh-Hans", "quota")][TestCase("zh-Hans", "part-failure")][TestCase("zh-Hans", "io-failure")][TestCase("zh-Hans", "network-failure")]
        [TestCase("zh-Hant", "quota")][TestCase("zh-Hant", "part-failure")][TestCase("zh-Hant", "io-failure")][TestCase("zh-Hant", "network-failure")]
        public async Task FailureDisplayUsesInstalledLocaleTablesWithoutPrivateDetails(string locale, string kind)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var localization = Type.GetType("VRVlog.LilToonExporter.ExporterLocalization, VRVlog.LilToonExporter.Compatibility", true);
            var translate = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), localization.GetMethod("T"));
            var localeField = localization.GetField("_locale", flags);
            var messagesField = localization.GetField("_messages", flags);
            var previousLocale = localeField.GetValue(null); var previousMessages = messagesField.GetValue(null);
            try
            {
                localeField.SetValue(null, locale); messagesField.SetValue(null, null);
                using (var fixture = new Fixture(17))
                using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
                {
                    fixture.Transport.Failure = kind;
                    try { await session.UploadAsync(); Assert.That(false, Is.True, "Must fail"); }
                    catch (InvalidOperationException) { }
                    var displayed = session.DisplayMessage(translate);
                    Assert.That(displayed != session.Message && !displayed.Contains("で停止しました"), Is.True);
                    Assert.That(Regex.IsMatch(displayed, @"[ぁ-ゟァ-ヿ]"), Is.False, "Every controlled reason and stage must be translated.");
                    if (kind == "quota") Assert.That(displayed.Contains("HTTP 429"), Is.True);
                    if (kind == "part-failure") Assert.That(displayed.Contains("HTTP 500"), Is.True);
                    Assert.That(new[] { "SECRET", fixture.Path, fixture.Transport.UploadToken, "private service failure" }.Any(value => displayed.Contains(value)), Is.False);
                }
            }
            finally { localeField.SetValue(null, previousLocale); messagesField.SetValue(null, previousMessages); }
        }
#endif

        [Test]
        public async Task EncryptedTemporaryFileIsReleasedAndCekForgottenOnDisposal()
        {
            using (var fixture = new Fixture(101))
            {
                var encrypted = await CloudEncryptedSnapshot.CreateAsync(fixture.Source, CancellationToken.None);
                var path = encrypted.SnapshotPath; var reader = encrypted.OpenRead();
                Assert.That(File.Exists(path), Is.True);
                encrypted.Dispose();
                Assert.That(File.Exists(path), Is.False); Assert.That(reader.CanRead, Is.False);
                Assert.That(encrypted.HasKey, Is.False);
                Assert.Throws<ObjectDisposedException>(() => encrypted.KeyForQr());
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
            }
        }

        [Test]
        public async Task CancellationDuringEncryptionClosesInputAndDeletesPartialCiphertext()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VRVlogEncryptedTransfers");
            var before = Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>();
            using (var fixture = new Fixture(101))
            using (var cancellation = new CancellationTokenSource())
            using (var reader = new InterruptedInput(fixture.Bytes))
            {
                var encrypting = CloudEncryptedSnapshot.CreateAsync(fixture.Source, cancellation.Token, testReader: () => reader);
                await reader.Blocked.Task;
                Assert.That(Directory.GetFiles(directory).Except(before).Count(), Is.EqualTo(1));
                cancellation.Cancel();
                try { await encrypting; Assert.That(false, Is.True); } catch (OperationCanceledException) { }
                Assert.That(reader.Disposed, Is.True);
                Assert.That(Directory.GetFiles(directory).Except(before).Any(), Is.False);
                Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(fixture.Bytes));
            }
        }

        [Test]
        public async Task PublishedQrKeepsOnlyTheKeyAndReleasesItsUploadedCiphertextFile()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VRVlogEncryptedTransfers");
            var before = Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>();
            using (var fixture = new Fixture(101))
            using (var session = new CloudVrmTransferSession(fixture.Source, fixture.Transport))
            {
                fixture.Transport.BeforePublish = () => Assert.That(Directory.GetFiles(directory).Except(before).Count(), Is.EqualTo(1));
                await session.UploadAsync();
                Assert.That(Directory.GetFiles(directory).Except(before).Any(), Is.False);
                Assert.That(session.HasEncryptionKey, Is.True);
                await session.CancelAsync();
                Assert.That(session.HasEncryptionKey, Is.False);
                Assert.That(session.Qr == null, Is.True);
            }
        }

        private sealed class InterruptedInput : Stream
        {
            private readonly byte[] bytes; private bool read;
            private readonly TaskCompletionSource<int> release = new TaskCompletionSource<int>();
            internal readonly TaskCompletionSource<bool> Blocked = new TaskCompletionSource<bool>();
            internal bool Disposed;
            internal InterruptedInput(byte[] bytes) { this.bytes = bytes; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            {
                if (!read) { read = true; Buffer.BlockCopy(bytes, 0, buffer, offset, 1); return Task.FromResult(1); }
                Blocked.TrySetResult(true); return release.Task;
            }
            protected override void Dispose(bool disposing) { Disposed = true; release.TrySetCanceled(); base.Dispose(disposing); }
            public override bool CanRead => !Disposed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => bytes.Length;
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [TestCase(1)][TestCase(15)][TestCase(16)][TestCase(17)][TestCase(33)][TestCase(257)][TestCase(65535)][TestCase(65536)][TestCase(65537)][TestCase(1048577)]
        public async Task RealStreamingEncryptionMatchesIndependentPythonVectors(int size)
        {
#if VRVLOG_LAN_TRANSFER_CLI
            var root = Directory.GetCurrentDirectory();
#else
            var root = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CloudVrmTransferSession).Assembly).resolvedPath;
#endif
            var json = File.ReadAllText(System.IO.Path.Combine(root, "Tests/LanTransfer/cloud-vrm-e2e-v3.json"));
            var cases = Regex.Matches(json, @"\{[^{}]*\}");
            Assert.That(cases.Count, Is.EqualTo(10));
            var vector = cases.Cast<Match>().Select(match => CloudTransferProtocol.Fields.Read(match.Value)).Single(fields => fields.Number("size") == size);
            var plain = new byte[size]; for (var i = 0; i < size; i++) plain[i] = (byte)((i * 37 + 11) % 256);
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrvlog-e2e-vector-" + Guid.NewGuid().ToString("N") + ".vrm");
            File.WriteAllBytes(path, plain);
            using (var source = new CloudVrmTransferSource(path, vector.Text("name")))
            using (var encrypted = await CloudEncryptedSnapshot.CreateAsync(source, CancellationToken.None, Unhex(vector.Text("keyHex")), Unhex(vector.Text("ivHex"))))
            {
                Assert.That(source.FileHash, Is.EqualTo(vector.Text("sha256")));
                Assert.That(LanTransferProtocol.Hex(CloudEncryptedSnapshot.Aad(source.Name, source.Size, source.FileHash)), Is.EqualTo(vector.Text("aadHex")));
                Assert.That(encrypted.Size, Is.EqualTo(vector.Number("wireSize")));
                Assert.That(encrypted.FileHash, Is.EqualTo(vector.Text("envelopeSha256")));
                Assert.That(encrypted.KeyForQr(), Is.EqualTo(vector.Text("key")));
                var envelope = File.ReadAllBytes(encrypted.SnapshotPath);
                Assert.That(LanTransferProtocol.Hex(envelope.Skip(envelope.Length - 32).ToArray()), Is.EqualTo(vector.Text("tagHex")));
                if (size <= 257) Assert.That(LanTransferProtocol.Hex(envelope), Is.EqualTo(vector.Text("envelopeHex")));
                Assert.That(AuthenticatedDecrypt(envelope, Unhex(vector.Text("keyHex")), source.Name, source.Size, source.FileHash), Is.EqualTo(plain));
            }
        }

        private static byte[] Unhex(string text)
        { var bytes = new byte[text.Length / 2]; for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16); return bytes; }

        private static byte[] DecodeKey(string key) => Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + "==");
        private static byte[] DecryptUploaded(FakeTransport transport, string qr)
        {
            var fields = CloudTransferProtocol.Fields.Read(qr.Substring(LanTransferProtocol.QrPrefix.Length));
            return AuthenticatedDecrypt(transport.Parts.SelectMany(part => part).ToArray(), DecodeKey(fields.Text("key")), fields.Text("name"), fields.Number("size"), fields.Text("sha256"));
        }
        private static byte[] AuthenticatedDecrypt(byte[] envelope, byte[] key, string name, long size, string hash)
        {
            if (key.Length != 64 || envelope.LongLength != CloudEncryptedSnapshot.WireSize(size) || Encoding.ASCII.GetString(envelope, 0, 8) != "VRVLOGE3") throw new CryptographicException();
            var aad = CloudEncryptedSnapshot.Aad(name, size, hash);
            byte[] expected;
            using (var mac = new HMACSHA512(key.Take(32).ToArray()))
            {
                mac.TransformBlock(aad, 0, aad.Length, null, 0);
                mac.TransformBlock(envelope, 8, envelope.Length - 40, null, 0);
                var bits = (ulong)aad.Length * 8; var length = new byte[8];
                for (var i = 0; i < 8; i++) length[7 - i] = (byte)(bits >> (8 * i));
                mac.TransformFinalBlock(length, 0, 8); expected = mac.Hash;
            }
            var mismatch = 0; for (var i = 0; i < 32; i++) mismatch |= expected[i] ^ envelope[envelope.Length - 32 + i];
            if (mismatch != 0) throw new CryptographicException(); // No AES operation before authentication.
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = key.Skip(32).ToArray(); aes.IV = envelope.Skip(8).Take(16).ToArray();
                using (var decrypt = aes.CreateDecryptor()) return decrypt.TransformFinalBlock(envelope, 24, envelope.Length - 56);
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vrvlog-cloud-test-" + Guid.NewGuid().ToString("N") + ".vrm");
            internal readonly byte[] Bytes;
            internal readonly CloudVrmTransferSource Source;
            internal readonly FakeTransport Transport;
            internal Fixture(int length, string name = "テストアバター.vrm")
            {
                Bytes = new byte[length]; for (var i = 0; i < length; i++) Bytes[i] = (byte)(i % 251);
                File.WriteAllBytes(Path, Bytes);
                Source = new CloudVrmTransferSource(Path, name); Transport = new FakeTransport(Source);
            }
            public void Dispose() => Source.Dispose();
        }

        private sealed class FakeTransport : ICloudTransferTransport
        {
            private readonly CloudVrmTransferSource source;
            internal readonly string ReadToken = LanTransferProtocol.Base64Url(new byte[32]);
            internal readonly string UploadToken = LanTransferProtocol.Base64Url(Enumerable.Repeat((byte)1, 32).ToArray());
            internal readonly List<byte[]> Parts = new List<byte[]>();
            internal readonly List<string> RequestTokens = new List<string>();
            internal string Metadata;
            internal string Failure, RemoteState = "ready";
            internal int CancelCalls, Created, PublishCalls;
            internal bool AuthenticatedCorrectly = true;
            internal Action BeforePublish, BeforePart;
            internal Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            internal int LifetimeSeconds = 180;
            internal TaskCompletionSource<bool> CreateRelease;
            internal readonly TaskCompletionSource<bool> CreateObserved = new TaskCompletionSource<bool>();
            internal TaskCompletionSource<bool> StatusRelease;
            internal bool StatusCancellationObserved;
            private string id, wireHash; private long expiry, wireSize;
            internal FakeTransport(CloudVrmTransferSource source) { this.source = source; }
            public async Task<CloudTransferResponse> SendAsync(string method, string path, string token, byte[] body, string contentType, CancellationToken cancellation)
            {
                RequestTokens.Add(token);
                Assert.That(path.StartsWith("/v3/", StringComparison.Ordinal), Is.True);
                if (path == "/v3/transfers")
                {
                    Assert.That(method, Is.EqualTo("POST")); Assert.That(token == null, Is.True);
                    if (Failure == "io-failure") throw new IOException("SECRET private path");
                    if (Failure == "network-failure") throw new System.Net.Http.HttpRequestException("SECRET token");
                    if (Failure == "quota") return new CloudTransferResponse(429, "{}");
                    if (Failure == "redirect") return new CloudTransferResponse(302, "{}");
                    Metadata = Encoding.UTF8.GetString(body);
                    var request = CloudTransferProtocol.Fields.Read(Metadata);
                    request.Exact("name", "size", "sha256");
                    var displayName = request.Text("name");
                    Assert.That(displayName, Is.EqualTo(CloudEncryptedSnapshot.WireName));
                    wireSize = request.Number("size"); wireHash = request.Text("sha256");
                    Assert.That(wireSize, Is.EqualTo(CloudEncryptedSnapshot.WireSize(source.Size))); Assert.That(wireHash == source.FileHash, Is.False);
                    Created++; id = Created.ToString("x32"); expiry = Now() + LifetimeSeconds;
                    CreateObserved.TrySetResult(true);
                    if (CreateRelease != null) await CreateRelease.Task;
                    return new CloudTransferResponse(201, "{\"v\":" + (Failure == "legacy-create" ? 2 : 3) + ",\"id\":\"" + id + "\",\"token\":\"" + ReadToken + "\",\"uploadToken\":\"" + UploadToken + "\",\"expiresAt\":" + expiry + ",\"partSize\":8388608}");
                }
                AuthenticatedCorrectly &= token == UploadToken;
                if (path.EndsWith("/cancel", StringComparison.Ordinal)) { CancelCalls++; return new CloudTransferResponse(200, "{}"); }
                cancellation.ThrowIfCancellationRequested();
                if (path.Contains("/parts/"))
                {
                    Assert.That(method, Is.EqualTo("PUT")); Assert.That(path, Is.EqualTo("/v3/transfers/" + id + "/parts/" + (Parts.Count + 1)));
                    if (Failure == "part-failure") return new CloudTransferResponse(500, "private service failure");
                    BeforePart?.Invoke();
                    Parts.Add((byte[])body.Clone()); return new CloudTransferResponse(200, "{}");
                }
                if (path.EndsWith("/publish", StringComparison.Ordinal))
                {
                    PublishCalls++;
                    BeforePublish?.Invoke();
                    var hash = Failure == "publish-mismatch" ? new string('f', 64) : wireHash;
                    using (var digest = SHA256.Create()) Assert.That(LanTransferProtocol.Hex(digest.ComputeHash(Parts.SelectMany(part => part).ToArray())), Is.EqualTo(wireHash));
                    return new CloudTransferResponse(200, "{\"v\":" + (Failure == "legacy-publish" ? 2 : 3) + ",\"id\":\"" + id + "\",\"token\":\"" + ReadToken + "\",\"name\":\"encrypted-avatar.bin\",\"size\":" + wireSize + ",\"sha256\":\"" + hash + "\",\"expiresAt\":" + expiry + "}");
                }
                Assert.That(method, Is.EqualTo("GET"));
                if (StatusRelease != null)
                    using (cancellation.Register(() => { StatusCancellationObserved = true; StatusRelease.TrySetCanceled(); }))
                        await StatusRelease.Task;
                return new CloudTransferResponse(Failure == "poll-failure" ? 503 : 200, "{\"state\":\"" + RemoteState + "\"}");
            }
        }
    }
}
