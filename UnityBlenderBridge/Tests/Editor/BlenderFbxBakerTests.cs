using BlenderBridge.Editor;
using NUnit.Framework;

namespace BlenderBridge.Tests
{
    public sealed class BlenderFbxBakerTests
    {
        [TestCase("../Outside.fbx")]
        [TestCase("Assets/Model.gltf")]
        [TestCase("Packages/Model.fbx")]
        [TestCase("Assets/../Outside.fbx")]
        [TestCase("Assets//Model.fbx")]
        [TestCase("Assets\\Model.fbx")]
        public void BakeRejectsNonProjectAndNonFbxInputs(string sourcePath)
        {
            Assert.Throws<System.ArgumentException>(() => BlenderFbxBaker.Bake(sourcePath));
        }

        [TestCase("Model:Final", "Model_Final")]
        [TestCase("  .  ", "BlenderAsset")]
        [TestCase("CON", "CON_")]
        public void SanitizeAssetNameProducesSafePathSegments(string source, string expected)
        {
            Assert.That(BlenderFbxBaker.SanitizeAssetName(source), Is.EqualTo(expected));
        }

        [Test]
        public void SanitizeAssetNameLimitsPathSegmentLength()
        {
            string result = BlenderFbxBaker.SanitizeAssetName(new string('A', 200));

            Assert.That(result.Length, Is.EqualTo(120));
        }
    }
}
