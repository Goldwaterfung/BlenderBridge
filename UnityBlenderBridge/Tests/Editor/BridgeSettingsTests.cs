using NUnit.Framework;
using UnityEngine;

namespace BlenderBridge.Tests
{
    public sealed class BridgeSettingsTests
    {
        private BridgeSettings settings;

        [SetUp]
        public void SetUp()
        {
            settings = ScriptableObject.CreateInstance<BridgeSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void DefaultsMatchBridgeConventions()
        {
            Assert.That(settings.Port, Is.EqualTo(8191));
            Assert.That(settings.OutputFolder, Is.EqualTo("Assets/Art/Blender"));
            Assert.That(settings.ColliderPolicy, Is.EqualTo(BridgeColliderPolicy.None));
            Assert.That(settings.MaximumPendingStateSlots,
                Is.EqualTo(BridgeSettings.DefaultMaximumPendingStateSlots));
            Assert.That(settings.MaximumQueuedControlEvents,
                Is.EqualTo(BridgeSettings.DefaultMaximumQueuedControlEvents));
            Assert.That(settings.MaximumPendingInboxBytes,
                Is.EqualTo(512L * 1024 * 1024));
            Assert.That(settings.TryValidate(out string error), Is.True, error);
        }

        [Test]
        public void RejectsOutputPathsOutsideProjectAssets()
        {
            var serialized = new UnityEditor.SerializedObject(settings);
            serialized.FindProperty("outputFolder").stringValue = "../Outside";
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("below Assets"));
        }

        [TestCase("Assets/Art/Bridge:Output")]
        [TestCase("Assets/Art/CON")]
        [TestCase("Assets/Art/Name.")]
        public void RejectsUnsafeOutputFolderSegments(string outputFolder)
        {
            var serialized = new UnityEditor.SerializedObject(settings);
            serialized.FindProperty("outputFolder").stringValue = outputFolder;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("below Assets"));
        }

        [Test]
        public void RejectsPortsOutsideTcpRange()
        {
            var serialized = new UnityEditor.SerializedObject(settings);
            serialized.FindProperty("port").intValue = 65536;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("between 1 and 65535"));
        }

        [Test]
        public void RejectsUnknownColliderPolicies()
        {
            var serialized = new UnityEditor.SerializedObject(settings);
            serialized.FindProperty("colliderPolicy").intValue = 99;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("collider policy"));
        }

        [Test]
        public void RejectsInboxLimitsOutsideDocumentedRanges()
        {
            var serialized = new UnityEditor.SerializedObject(settings);
            serialized.FindProperty("maximumPendingStateSlots").intValue = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("pending state slot limit"));

            serialized.FindProperty("maximumPendingStateSlots").intValue =
                BridgeSettings.DefaultMaximumPendingStateSlots;
            serialized.FindProperty("maximumQueuedControlEvents").intValue =
                BridgeSettings.MaximumQueuedControlEventsLimit + 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out error), Is.False);
            Assert.That(error, Does.Contain("ordered control event limit"));

            serialized.FindProperty("maximumQueuedControlEvents").intValue =
                BridgeSettings.DefaultMaximumQueuedControlEvents;
            serialized.FindProperty("maximumPendingInboxMegabytes").intValue = 15;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(settings.TryValidate(out error), Is.False);
            Assert.That(error, Does.Contain("pending inbox memory limit"));
        }
    }
}
