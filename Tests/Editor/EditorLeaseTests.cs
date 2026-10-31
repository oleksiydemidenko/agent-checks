using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace AgentChecks.Editor.Tests
{
    public sealed class EditorLeaseTests
    {
        private const string Holder = "editor-lease-test";
        private const string Other = "editor-lease-test-other";

        [TearDown]
        public void TearDown()
        {
            EditorLease.Release(Holder);
            EditorLease.Release(Other);
        }

        [UnityTest]
        public IEnumerator LeaseSurvivesDomainReloadBlocksOthersAndExpires()
        {
            Assume.That(EditorLease.Current, Is.Null, "A real lease is held; run this test when the editor lease is free.");
            Assert.That(EditorLease.TryAcquire(Holder, "test", 600, out _), Is.True);

            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();

            Assert.That(EditorLease.TryAcquire(Other, "test", 600, out var busy), Is.False);
            Assert.That(busy.Client, Is.EqualTo(Holder));
            Assert.That(EditorLease.Release(Other), Is.False);
            Assert.That(EditorLease.TryAcquire(Holder, "renew", 1, out _), Is.True);

            var expiry = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < expiry)
                yield return null;

            Assert.That(EditorLease.Current, Is.Null);
            Assert.That(EditorLease.TryAcquire(Other, "test", 600, out _), Is.True);
            Assert.That(EditorLease.Release(Other), Is.True);
        }
    }
}
