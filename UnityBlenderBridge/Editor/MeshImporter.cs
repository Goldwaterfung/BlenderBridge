using System;
using BlenderBridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlenderBridge.Editor
{
    internal static class MeshImporter
    {
        public static Mesh Build(MeshPayload payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            var mesh = new Mesh
            {
                name = string.IsNullOrEmpty(payload.Name)
                    ? $"Blender Mesh {payload.ObjectId:N}"
                    : payload.Name,
                indexFormat = payload.Positions.Length > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };

            try
            {
                mesh.MarkDynamic();
                var positions = new Vector3[payload.Positions.Length];
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = ToUnity(payload.Positions[i]);
                mesh.vertices = positions;

                if ((payload.AttributeFlags & MeshAttributeFlags.Normals) != 0)
                {
                    var normals = new Vector3[payload.Normals.Length];
                    for (int i = 0; i < normals.Length; i++)
                        normals[i] = ToUnity(payload.Normals[i]);
                    mesh.normals = normals;
                }

                if ((payload.AttributeFlags & MeshAttributeFlags.Tangents) != 0)
                {
                    var tangents = new Vector4[payload.Tangents.Length];
                    for (int i = 0; i < tangents.Length; i++)
                    {
                        Vector4Value tangent = payload.Tangents[i];
                        tangents[i] = new Vector4(tangent.X, tangent.Y, tangent.Z, tangent.W);
                    }
                    mesh.tangents = tangents;
                }

                if ((payload.AttributeFlags & MeshAttributeFlags.Uv0) != 0)
                {
                    var uvs = new Vector2[payload.Uvs.Length];
                    for (int i = 0; i < uvs.Length; i++)
                        uvs[i] = new Vector2(payload.Uvs[i].X, payload.Uvs[i].Y);
                    mesh.uv = uvs;
                }

                if ((payload.AttributeFlags & MeshAttributeFlags.Colors) != 0)
                {
                    var colors = new Color32[payload.Colors.Length];
                    for (int i = 0; i < colors.Length; i++)
                    {
                        Rgba32Value color = payload.Colors[i];
                        colors[i] = new Color32(color.R, color.G, color.B, color.A);
                    }
                    mesh.colors32 = colors;
                }

                mesh.subMeshCount = payload.Submeshes.Length;
                var allIndices = new int[payload.Indices.Length];
                for (int i = 0; i < allIndices.Length; i++)
                    allIndices[i] = checked((int)payload.Indices[i]);

                for (int i = 0; i < payload.Submeshes.Length; i++)
                {
                    SubmeshPayload submesh = payload.Submeshes[i];
                    mesh.SetIndices(
                        allIndices,
                        checked((int)submesh.IndexOffset),
                        checked((int)submesh.IndexCount),
                        MeshTopology.Triangles,
                        i,
                        false);
                }

                Vector3 boundsCenter = ToUnity(payload.BoundsCenter);
                Vector3 boundsExtents = ToUnity(payload.BoundsExtents);
                if (!IsFinite(boundsCenter.x - boundsExtents.x) ||
                    !IsFinite(boundsCenter.y - boundsExtents.y) ||
                    !IsFinite(boundsCenter.z - boundsExtents.z) ||
                    !IsFinite(boundsCenter.x + boundsExtents.x) ||
                    !IsFinite(boundsCenter.y + boundsExtents.y) ||
                    !IsFinite(boundsCenter.z + boundsExtents.z) ||
                    !IsFinite(boundsExtents.x * 2f) ||
                    !IsFinite(boundsExtents.y * 2f) ||
                    !IsFinite(boundsExtents.z * 2f))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(payload),
                        "Mesh bounds extents are too large for Unity bounds.");
                }

                mesh.bounds = new Bounds(
                    boundsCenter,
                    boundsExtents * 2f);
                mesh.UploadMeshData(false);
                return mesh;
            }
            catch (UnityException)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
            catch (ArgumentException)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
            catch (InvalidOperationException)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
            catch (OverflowException)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        public static Material[] ResolveMaterials(Guid objectId, MeshPayload payload)
        {
            var materialIndices = new uint[payload.Submeshes.Length];
            for (int i = 0; i < materialIndices.Length; i++)
                materialIndices[i] = payload.Submeshes[i].MaterialIndex;
            return ResolveMaterials(objectId, materialIndices);
        }

        public static Material[] ResolveMaterials(Guid objectId, uint[] materialIndices)
        {
            var materials = new Material[materialIndices.Length];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = LiveMaterialResolver.Resolve(objectId, materialIndices[i]);
            return materials;
        }

        private static Vector3 ToUnity(Vector3Value value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
