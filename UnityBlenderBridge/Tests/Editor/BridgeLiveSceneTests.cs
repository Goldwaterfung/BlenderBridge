using System;
using BlenderBridge;
using BlenderBridge.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlenderBridge.Tests
{
    public sealed class BridgeLiveSceneTests
    {
        private static readonly Guid ParentId =
            new Guid("10000000-0000-0000-0000-000000000001");
        private static readonly Guid ChildId =
            new Guid("10000000-0000-0000-0000-000000000002");

        [SetUp]
        public void SetUp()
        {
            BridgeLiveScene.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            BridgeLiveScene.Clear();
        }

        [Test]
        public void AppliesHierarchyBeforeTransformAndCreatesMissingAncestor()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Transform, new TransformPayload
                {
                    ObjectId = ChildId,
                    Name = "Child",
                    LocalPosition = new Vector3Value(1, 2, 3),
                    LocalRotation = new Vector4Value(0, 0, 0, 1),
                    LocalScale = new Vector3Value(1, 1, 1)
                }),
                Message(BridgePacketType.Hierarchy, new HierarchyPayload
                {
                    ObjectId = ChildId,
                    Name = "Child",
                    ParentId = ParentId,
                    SiblingIndex = 0
                })
            });

            Assert.That(BridgeLiveScene.LiveObjectCount, Is.EqualTo(2));
            Assert.That(BridgeLiveScene.TryGetGameObject(ParentId, out GameObject parent), Is.True);
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject child), Is.True);
            Assert.That(child.transform.parent, Is.EqualTo(parent.transform));
            Assert.That(child.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(child.name, Is.EqualTo("Child"));
            Assert.That((child.hideFlags & HideFlags.DontSaveInEditor) != 0, Is.True);
        }

        [Test]
        public void VisibilityChangesSceneViewVisibilityWithoutDeactivatingObject()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Visibility, new VisibilityPayload
                {
                    ObjectId = ChildId,
                    Name = "Hidden",
                    Visible = false
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Assert.That(gameObject.activeSelf, Is.True);
            Assert.That(SceneVisibilityManager.instance.IsHidden(gameObject), Is.True);

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Visibility, new VisibilityPayload
                {
                    ObjectId = ChildId,
                    Name = "Visible",
                    Visible = true
                })
            });

            Assert.That(gameObject.activeSelf, Is.True);
            Assert.That(SceneVisibilityManager.instance.IsHidden(gameObject), Is.False);
        }

        [Test]
        public void DeletingParentDetachesSurvivingChildren()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Hierarchy, new HierarchyPayload
                {
                    ObjectId = ParentId,
                    Name = "Parent",
                    ParentId = Guid.Empty,
                    SiblingIndex = 0
                }),
                Message(BridgePacketType.Hierarchy, new HierarchyPayload
                {
                    ObjectId = ChildId,
                    Name = "Child",
                    ParentId = ParentId,
                    SiblingIndex = 0
                })
            });

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.ObjectDeleted, new ObjectDeletedPayload
                {
                    ObjectId = ParentId
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ParentId, out _), Is.False);
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject child), Is.True);
            Assert.That(child.transform.parent.name, Is.EqualTo("__BlenderLive"));
        }

        [Test]
        public void MeshImportTransfersChannelsSubmeshesBoundsAndHelloMaterialMetadata()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Hello, new HelloPayload
                {
                    MetadataJson =
                        "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Preview Red\",\"base_color\":[0.8,0.2,0.1,1.0],\"metallic\":0.4,\"smoothness\":0.7}]}"
                }),
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1))
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Mesh mesh = gameObject.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(mesh.vertexCount, Is.EqualTo(3));
            Assert.That(mesh.normals.Length, Is.EqualTo(3));
            Assert.That(mesh.tangents.Length, Is.EqualTo(3));
            Assert.That(mesh.uv.Length, Is.EqualTo(3));
            Assert.That(mesh.colors32.Length, Is.EqualTo(3));
            Assert.That(mesh.subMeshCount, Is.EqualTo(1));
            Assert.That(mesh.GetIndices(0), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(mesh.bounds.center, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(mesh.bounds.size, Is.EqualTo(new Vector3(2, 4, 6)));

            Material material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(material.name, Is.EqualTo("Blender Preview - Preview Red"));
            string colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            Color materialColor = material.GetColor(colorProperty);
            Assert.That(materialColor.r, Is.EqualTo(0.8f).Within(0.001f));
            Assert.That(materialColor.g, Is.EqualTo(0.2f).Within(0.001f));

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson =
                        "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Updated Material\",\"base_color\":[0.1,0.3,0.9,1.0],\"metallic\":0.2,\"smoothness\":0.6,\"textures\":{}}]}"
                })
            });

            material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(material.name, Is.EqualTo("Blender Preview - Updated Material"));
            colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            materialColor = material.GetColor(colorProperty);
            Assert.That(materialColor.b, Is.EqualTo(0.9f).Within(0.001f));
        }

        [Test]
        public void MaterialUpdateLoadsEmbeddedBaseColorTextureFallback()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson =
                        "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"missing.png\",\"prefer_embedded\":true,\"png_base64\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==\"}}}]}"
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Material material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            string textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            Texture2D texture = material.GetTexture(textureProperty) as Texture2D;

            Assert.That(texture, Is.Not.Null);
            Assert.That(texture.width, Is.EqualTo(1));
            Assert.That(texture.height, Is.EqualTo(1));
        }

        [Test]
        public void SnapshotReplacementReusesCachedMaterialAndTexture()
        {
            const string texturesJson =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"missing.png\",\"prefer_embedded\":true,\"png_base64\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==\"}}}]}";
            const string helloJson =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5}]}";

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = texturesJson
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Material originalMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(originalMaterial, Is.Not.Null);
            string textureProperty = originalMaterial.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            Texture2D originalTexture = originalMaterial.GetTexture(textureProperty) as Texture2D;
            Assert.That(originalTexture, Is.Not.Null);

            BridgeLiveScene.ReplaceSnapshot(new[]
            {
                Message(BridgePacketType.Hello, new HelloPayload
                {
                    MetadataJson = helloJson,
                    ObjectCount = 1,
                    Objects = new[] { new HelloObject { ObjectId = ChildId } }
                }),
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = texturesJson
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out gameObject), Is.True);
            Material restoredMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(restoredMaterial, Is.SameAs(originalMaterial));
            Assert.That(restoredMaterial.GetTexture(textureProperty), Is.SameAs(originalTexture));
        }

        [Test]
        public void MaterialUpdateReusesTextureWhenOnlyItsHashIsResent()
        {
            const string fullMetadata =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"missing.png\",\"prefer_embedded\":true,\"hash\":\"abc123\",\"png_base64\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==\"}}}]}";
            const string hashOnlyMetadata =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[0.5,0.5,0.5,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"missing.png\",\"prefer_embedded\":true,\"hash\":\"abc123\",\"png_base64\":\"\"}}}]}";

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = fullMetadata
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Material material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            string textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            Texture2D texture = material.GetTexture(textureProperty) as Texture2D;
            Assert.That(texture, Is.Not.Null);

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = hashOnlyMetadata
                })
            });

            Material updatedMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(updatedMaterial, Is.SameAs(material));
            Assert.That(updatedMaterial.GetTexture(textureProperty), Is.SameAs(texture));
        }

        [Test]
        public void HashOnlyMetadataWithNewHashKeepsTextureUntilFullPayloadArrives()
        {
            const string embeddedPng =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";
            const string repairedPng =
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNg+M8AAAICAQB7CYF4AAAAAElFTkSuQmCC";
            string fullMetadata =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"\",\"prefer_embedded\":true,\"hash\":\"abc123\",\"png_base64\":\"" +
                embeddedPng +
                "\"}}}]}";
            const string hashOnlyMetadata =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[0.5,0.5,0.5,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"\",\"prefer_embedded\":true,\"hash\":\"def456\",\"png_base64\":\"\"}}}]}";
            string repairedMetadata =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Textured\",\"base_color\":[0.5,0.5,0.5,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"base_color\":{\"path\":\"\",\"prefer_embedded\":true,\"hash\":\"def456\",\"png_base64\":\"" +
                repairedPng +
                "\"}}}]}";

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = fullMetadata
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Material material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            string textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            Texture2D originalTexture = material.GetTexture(textureProperty) as Texture2D;
            Assert.That(originalTexture, Is.Not.Null);

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = hashOnlyMetadata
                })
            });

            Material updatedMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(updatedMaterial, Is.SameAs(material));
            Assert.That(updatedMaterial.GetTexture(textureProperty), Is.SameAs(originalTexture));

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = repairedMetadata
                })
            });

            Texture2D repairedTexture =
                updatedMaterial.GetTexture(textureProperty) as Texture2D;
            Assert.That(repairedTexture, Is.Not.Null);
            Assert.That(repairedTexture, Is.Not.SameAs(originalTexture));
        }

        [Test]
        public void MaterialUpdateImportsEmbeddedNormalMapAsUnityNormalMap()
        {
            const string metadataJson =
                "{\"materials\":[{\"object_uuid\":\"10000000-0000-0000-0000-000000000002\",\"index\":3,\"name\":\"Normal Mapped\",\"base_color\":[1,1,1,1],\"metallic\":0,\"smoothness\":0.5,\"textures\":{\"normal\":{\"path\":\"missing-normal.png\",\"prefer_embedded\":true,\"png_base64\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==\"}}}]}";
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = metadataJson
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Material material = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Texture2D normalMap = material.GetTexture("_BumpMap") as Texture2D;

            Assert.That(normalMap, Is.Not.Null);
            string assetPath = AssetDatabase.GetAssetPath(normalMap);
            Assert.That(assetPath, Does.StartWith("Assets/BlenderBridgeLiveSyncCache/"));
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            Assert.That(importer, Is.Not.Null);
            Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(importer.sRGBTexture, Is.False);

            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = metadataJson
                })
            });
            Assert.That(gameObject.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(material));
            Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(normalMap));

            string changedColorMetadata = metadataJson.Replace(
                "\"base_color\":[1,1,1,1]",
                "\"base_color\":[0.5,0.5,0.5,1]");
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Materials, new MaterialsPayload
                {
                    ObjectId = ChildId,
                    Name = "Triangle",
                    MetadataJson = changedColorMetadata
                })
            });
            Material updatedMaterial = gameObject.GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(updatedMaterial, Is.SameAs(material));
            Assert.That(updatedMaterial.GetTexture("_BumpMap"), Is.SameAs(normalMap));
        }

        [Test]
        public void MeshImportClearsOmittedChannelsAndUsesUInt32Above65535Vertices()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 1)),
                Message(BridgePacketType.MeshFull, new MeshPayload
                {
                    ObjectId = ChildId,
                    Name = "Large mesh",
                    ContentHash = 2,
                    Positions = new Vector3Value[ushort.MaxValue + 1],
                    Indices = new uint[] { 0, 1, 2 },
                    Submeshes = new[]
                    {
                        new SubmeshPayload { IndexOffset = 0, IndexCount = 3, MaterialIndex = 0 }
                    },
                    BoundsExtents = new Vector3Value(1, 1, 1)
                })
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Mesh mesh = gameObject.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(mesh.indexFormat, Is.EqualTo(IndexFormat.UInt32));
            Assert.That(mesh.vertexCount, Is.EqualTo(ushort.MaxValue + 1));
            Assert.That(mesh.normals.Length, Is.Zero);
            Assert.That(mesh.tangents.Length, Is.Zero);
            Assert.That(mesh.uv.Length, Is.Zero);
            Assert.That(mesh.colors32.Length, Is.Zero);
        }

        [Test]
        public void UnchangedMeshHashDoesNotReplaceMeshInstance()
        {
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(ChildId, 7))
            });
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject gameObject), Is.True);
            Mesh original = gameObject.GetComponent<MeshFilter>().sharedMesh;

            MeshPayload sameHash = TriangleMesh(ChildId, 7);
            sameHash.Positions[0] = new Vector3Value(100, 100, 100);
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.MeshFull, sameHash)
            });

            Assert.That(gameObject.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(original));
        }

        [Test]
        public void SnapshotStagingKeepsCommittedSceneVisibleUntilCommit()
        {
            Guid stagedId = new Guid("10000000-0000-0000-0000-000000000003");
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Transform, Transform(ChildId, "Committed"))
            });
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject committed), Is.True);

            BridgeLiveScene.BeginSnapshotReplacement();
            BridgeLiveScene.ApplySnapshotMessages(new[]
            {
                Message(BridgePacketType.MeshFull, TriangleMesh(stagedId, 1))
            });

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject stillCommitted), Is.True);
            Assert.That(stillCommitted, Is.SameAs(committed));
            Assert.That(BridgeLiveScene.TryGetGameObject(stagedId, out _), Is.False);

            BridgeLiveScene.CommitSnapshotReplacement();

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out _), Is.False);
            Assert.That(BridgeLiveScene.TryGetGameObject(stagedId, out GameObject staged), Is.True);
            Assert.That(staged.activeInHierarchy, Is.True);
            Assert.That(staged.GetComponent<MeshRenderer>().enabled, Is.True);
            Assert.That(staged.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(3));
            BridgeLiveScene.CleanupRetiredScenes(1);
            Assert.That(committed == null, Is.True);

            BridgeLiveScene.Clear();
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Transform, Transform(ChildId, "After snapshot"))
            });
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject recreated), Is.True);
            Assert.That(recreated.activeInHierarchy, Is.True);
        }

        [Test]
        public void AbortingSnapshotStagingLeavesCommittedSceneUntouched()
        {
            Guid stagedId = new Guid("10000000-0000-0000-0000-000000000004");
            BridgeLiveScene.ApplyMessages(new[]
            {
                Message(BridgePacketType.Transform, Transform(ChildId, "Committed"))
            });
            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject committed), Is.True);

            BridgeLiveScene.BeginSnapshotReplacement();
            BridgeLiveScene.ApplySnapshotMessages(new[]
            {
                Message(BridgePacketType.Transform, Transform(stagedId, "Staged"))
            });
            BridgeLiveScene.AbortSnapshotReplacement();

            Assert.That(BridgeLiveScene.TryGetGameObject(ChildId, out GameObject stillCommitted), Is.True);
            Assert.That(stillCommitted, Is.SameAs(committed));
            Assert.That(BridgeLiveScene.TryGetGameObject(stagedId, out _), Is.False);
            BridgeLiveScene.CleanupRetiredScenes(1);
        }

        private static BridgeInboundMessage Message(BridgePacketType type, BridgePayload payload)
        {
            var inbox = new BridgeInbox(8, 8, 16L * 1024 * 1024);
            inbox.Enqueue(0, type, payload);
            return inbox.Drain(1)[0];
        }

        private static TransformPayload Transform(Guid objectId, string name)
        {
            return new TransformPayload
            {
                ObjectId = objectId,
                Name = name,
                LocalPosition = new Vector3Value(1, 2, 3),
                LocalRotation = new Vector4Value(0, 0, 0, 1),
                LocalScale = new Vector3Value(1, 1, 1)
            };
        }

        private static MeshPayload TriangleMesh(Guid objectId, uint contentHash)
        {
            return new MeshPayload
            {
                ObjectId = objectId,
                Name = "Triangle",
                ContentHash = contentHash,
                AttributeFlags =
                    MeshAttributeFlags.Normals |
                    MeshAttributeFlags.Uv0 |
                    MeshAttributeFlags.Tangents |
                    MeshAttributeFlags.Colors,
                Positions = new[]
                {
                    new Vector3Value(0, 0, 0),
                    new Vector3Value(1, 0, 0),
                    new Vector3Value(0, 1, 0)
                },
                Normals = new[]
                {
                    new Vector3Value(0, 0, 1),
                    new Vector3Value(0, 0, 1),
                    new Vector3Value(0, 0, 1)
                },
                Tangents = new[]
                {
                    new Vector4Value(1, 0, 0, 1),
                    new Vector4Value(1, 0, 0, 1),
                    new Vector4Value(1, 0, 0, 1)
                },
                Uvs = new[]
                {
                    new Vector2Value(0, 0),
                    new Vector2Value(1, 0),
                    new Vector2Value(0, 1)
                },
                Colors = new[]
                {
                    new Rgba32Value(255, 0, 0, 255),
                    new Rgba32Value(0, 255, 0, 255),
                    new Rgba32Value(0, 0, 255, 255)
                },
                Indices = new uint[] { 0, 1, 2 },
                Submeshes = new[]
                {
                    new SubmeshPayload { IndexOffset = 0, IndexCount = 3, MaterialIndex = 3 }
                },
                BoundsCenter = new Vector3Value(1, 2, 3),
                BoundsExtents = new Vector3Value(1, 2, 3)
            };
        }
    }
}
