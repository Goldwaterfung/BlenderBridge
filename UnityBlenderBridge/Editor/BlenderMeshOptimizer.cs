using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlenderBridge.Editor
{
    internal static class BlenderMeshOptimizer
    {
        internal sealed class MeshAssetRecord
        {
            public string HierarchyPath;
            public string AssetPath;
            public string VertexFormat;
            public double MaximumTexelError;
            public int LimitingTextureWidth;
            public int LimitingTextureHeight;
            public VertexAttributeFormat[] UvFormats;
            public int[] UvDimensions;
            public string Uv0Fingerprint;
        }

        private sealed class AttributeData
        {
            public VertexAttribute Attribute;
            public int Dimension;
            public int Stream;
            public VertexAttributeFormat Format;
            public float[] Values;
        }

        internal static List<MeshAssetRecord> CreatePersistentMeshes(
            GameObject model,
            string sourceAssetPath,
            string meshesFolder,
            int maximumTextureDimension,
            bool allowFloat32Override,
            MeshAssetTransaction transaction)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            List<BlenderUv0Preflight.ValidationResult> validations =
                BlenderUv0Preflight.ValidateModel(
                    sourceAssetPath,
                    model,
                    maximumTextureDimension,
                    allowFloat32Override);
            MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length != validations.Count)
                throw new InvalidDataException("UV0 preflight did not return one validation result per mesh renderer.");

            var result = new List<MeshAssetRecord>(renderers.Length);
            EnsureAssetFolder(meshesFolder);
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                MeshRenderer renderer = renderers[rendererIndex];
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh source = filter == null ? null : filter.sharedMesh;
                string hierarchyPath = GetHierarchyPath(renderer.transform, model.transform);
                if (source == null)
                    throw new InvalidDataException($"Mesh renderer '{hierarchyPath}' has no source mesh.");

                BlenderUv0Preflight.ValidationResult validation = validations[rendererIndex];
                Mesh converted = CreateOptimizedMesh(source, hierarchyPath, validation);
                string assetPath = GetMeshAssetPath(meshesFolder, hierarchyPath);
                Mesh persistentMesh = transaction.SaveMesh(assetPath, converted);
                filter.sharedMesh = persistentMesh;

                result.Add(new MeshAssetRecord
                {
                    HierarchyPath = hierarchyPath,
                    AssetPath = assetPath,
                    VertexFormat = validation.UsesFloat32Override ? "Float32" : "UNorm16",
                    MaximumTexelError = validation.MaximumTexelError,
                    LimitingTextureWidth = validation.LimitingResolution.Width,
                    LimitingTextureHeight = validation.LimitingResolution.Height,
                    UvFormats = GetUvFormats(persistentMesh),
                    UvDimensions = GetUvDimensions(persistentMesh),
                    Uv0Fingerprint = ComputeUv0Fingerprint(persistentMesh)
                });
            }

            return result;
        }

        internal static void ValidateSavedMeshes(
            string prefabPath,
            IList<MeshAssetRecord> expectedMeshes,
            int maximumTextureDimension,
            bool allowFloat32Override)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new InvalidDataException($"Could not reload baked Prefab '{prefabPath}' for mesh verification.");

            var renderersByPath = prefab.GetComponentsInChildren<MeshRenderer>(true)
                .ToDictionary(
                    renderer => GetHierarchyPath(renderer.transform, prefab.transform),
                    renderer => renderer,
                    StringComparer.Ordinal);
            if (renderersByPath.Count != expectedMeshes.Count)
            {
                throw new InvalidDataException(
                    $"Saved Prefab '{prefabPath}' has {renderersByPath.Count} mesh renderers; expected {expectedMeshes.Count} optimized meshes.");
            }

            foreach (MeshAssetRecord expected in expectedMeshes)
            {
                if (!renderersByPath.TryGetValue(expected.HierarchyPath, out MeshRenderer renderer))
                {
                    throw new InvalidDataException(
                        $"Saved Prefab '{prefabPath}' is missing optimized mesh renderer '{expected.HierarchyPath}'.");
                }

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter == null ? null : filter.sharedMesh;
                string actualPath = mesh == null ? string.Empty : AssetDatabase.GetAssetPath(mesh);
                if (mesh == null || !string.Equals(actualPath, expected.AssetPath, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Saved Prefab '{prefabPath}' renderer '{expected.HierarchyPath}' does not reference optimized mesh asset '{expected.AssetPath}'.");
                }

                for (int channel = 0; channel < 8; channel++)
                {
                    VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                    bool expectedPresent = expected.UvDimensions[channel] > 0;
                    if (mesh.HasVertexAttribute(attribute) != expectedPresent)
                    {
                        throw new InvalidDataException(
                            $"Saved mesh '{actualPath}' changed UV channel {channel} presence.");
                    }
                    if (!expectedPresent)
                        continue;

                    int expectedStream = channel == 0 ? 1 : 2;
                    if (mesh.GetVertexAttributeFormat(attribute) != expected.UvFormats[channel] ||
                        mesh.GetVertexAttributeDimension(attribute) != expected.UvDimensions[channel] ||
                        mesh.GetVertexAttributeStream(attribute) != expectedStream)
                    {
                        throw new InvalidDataException(
                            $"Saved mesh '{actualPath}' UV{channel} layout does not match its validated format, dimension, and stream.");
                    }
                }

                if (mesh.GetVertexBufferStride(1) != 4)
                    throw new InvalidDataException(
                        $"Saved mesh '{actualPath}' UV0 stream does not occupy exactly 4 bytes per vertex.");
                if (!string.Equals(
                        ComputeUv0Fingerprint(mesh),
                        expected.Uv0Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Saved mesh '{actualPath}' UV0 values changed after Prefab serialization.");
                }
                ValidateGeometryStream(mesh, actualPath);
            }

            BlenderUv0Preflight.ValidateModel(
                prefabPath,
                prefab,
                maximumTextureDimension,
                allowFloat32Override);
        }

        internal static Mesh CreateOptimizedMeshForTests(
            Mesh source,
            IList<BlenderUv0Preflight.TextureResolution> resolutions,
            bool allowFloat32Override)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            var coordinates = new List<Vector4>();
            source.GetUVs(0, coordinates);
            BlenderUv0Preflight.ValidationResult validation =
                BlenderUv0Preflight.ValidateCoordinates(
                    source.name,
                    "UV0",
                    coordinates,
                    resolutions,
                    allowFloat32Override);
            return CreateOptimizedMesh(source, source.name, validation);
        }

        private static Mesh CreateOptimizedMesh(
            Mesh source,
            string hierarchyPath,
            BlenderUv0Preflight.ValidationResult validation)
        {
            if (source.blendShapeCount > 0 ||
                source.HasVertexAttribute(VertexAttribute.BlendIndices) ||
                source.HasVertexAttribute(VertexAttribute.BlendWeight))
            {
                throw new InvalidDataException(
                    $"Mesh '{hierarchyPath}' contains blend-shape or skinning data that the static optimized layout cannot preserve.");
            }

            var attributes = ReadAttributes(source, validation, hierarchyPath);
            var descriptors = attributes.Select(attribute =>
                new VertexAttributeDescriptor(
                    attribute.Attribute,
                    attribute.Format,
                    attribute.Dimension,
                    attribute.Stream)).ToArray();
            Mesh optimized = new Mesh
            {
                name = source.name + "_Optimized",
                indexFormat = source.indexFormat,
                subMeshCount = source.subMeshCount
            };

            try
            {
                VertexAttributeFormat uv0Format = validation.UsesFloat32Override
                    ? VertexAttributeFormat.Float32
                    : VertexAttributeFormat.UNorm16;
                if (!SystemInfo.SupportsVertexAttributeFormat(uv0Format, 2))
                {
                    throw new InvalidDataException(
                        $"The current Unity device '{SystemInfo.deviceName}' " +
                        $"({SystemInfo.graphicsDeviceType}, {SystemInfo.deviceType}) does not support UV0 " +
                        $"{uv0Format} dimension 2 required by mesh '{hierarchyPath}'.");
                }

                optimized.SetVertexBufferParams(source.vertexCount, descriptors);
                WriteVertexBuffers(optimized, attributes, source.vertexCount);
                for (int submesh = 0; submesh < source.subMeshCount; submesh++)
                {
                    optimized.SetIndices(
                        source.GetIndices(submesh, false),
                        source.GetTopology(submesh),
                        submesh,
                        false,
                        checked((int)source.GetBaseVertex(submesh)));
                }
                optimized.bounds = source.bounds;
                ValidateConvertedMesh(source, optimized, validation, hierarchyPath);
                return optimized;
            }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(optimized);
                if (exception is InvalidDataException)
                    throw;
                throw new InvalidDataException(
                    $"Could not build the optimized vertex layout for mesh '{hierarchyPath}' on " +
                    $"device '{SystemInfo.deviceName}' ({SystemInfo.graphicsDeviceType}): {exception.Message}",
                    exception);
            }
        }

        private static List<AttributeData> ReadAttributes(
            Mesh source,
            BlenderUv0Preflight.ValidationResult validation,
            string hierarchyPath)
        {
            var attributes = new List<AttributeData>();
            var positions = new List<Vector3>();
            source.GetVertices(positions);
            if (positions.Count != source.vertexCount ||
                source.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has no readable position data.");
            attributes.Add(new AttributeData
            {
                Attribute = VertexAttribute.Position,
                Dimension = source.GetVertexAttributeDimension(VertexAttribute.Position),
                Stream = 0,
                Format = source.GetVertexAttributeFormat(VertexAttribute.Position),
                Values = Flatten(positions, source.GetVertexAttributeDimension(VertexAttribute.Position))
            });

            AddVector3Attribute(source, attributes, VertexAttribute.Normal, positions.Count, hierarchyPath);
            AddVector4Attribute(source, attributes, VertexAttribute.Tangent, positions.Count, hierarchyPath);
            AddColorAttribute(source, attributes, positions.Count, hierarchyPath);

            var uv0 = new List<Vector4>();
            source.GetUVs(0, uv0);
            if (uv0.Count != source.vertexCount ||
                source.GetVertexAttributeDimension(VertexAttribute.TexCoord0) != 2)
            {
                throw new InvalidDataException(
                    $"Mesh '{hierarchyPath}' UV0 must be a two-component channel with one coordinate per vertex.");
            }

            float[] uv0Values = new float[uv0.Count * 2];
            for (int i = 0; i < uv0.Count; i++)
            {
                if (validation.UsesFloat32Override)
                {
                    uv0Values[i * 2] = uv0[i].x;
                    uv0Values[i * 2 + 1] = uv0[i].y;
                }
                else
                {
                    uv0Values[i * 2] = (float)(Quantize(uv0[i].x) /
                                               BlenderUv0Preflight.Unorm16Maximum);
                    uv0Values[i * 2 + 1] = (float)(Quantize(uv0[i].y) /
                                                   BlenderUv0Preflight.Unorm16Maximum);
                }
            }
            attributes.Add(new AttributeData
            {
                Attribute = VertexAttribute.TexCoord0,
                Dimension = 2,
                Stream = 1,
                Format = validation.UsesFloat32Override
                    ? VertexAttributeFormat.Float32
                    : VertexAttributeFormat.UNorm16,
                Values = uv0Values
            });

            for (int channel = 1; channel < 8; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!source.HasVertexAttribute(attribute))
                    continue;
                int dimension = source.GetVertexAttributeDimension(attribute);
                if (dimension < 1 || dimension > 4)
                    throw new InvalidDataException(
                        $"Mesh '{hierarchyPath}' UV channel {channel} has unsupported dimension {dimension}.");

                var values = new List<Vector4>();
                source.GetUVs(channel, values);
                if (values.Count != source.vertexCount)
                    throw new InvalidDataException(
                        $"Mesh '{hierarchyPath}' UV channel {channel} cannot be read for every vertex.");
                attributes.Add(new AttributeData
                {
                    Attribute = attribute,
                    Dimension = dimension,
                    Stream = 2,
                    Format = source.GetVertexAttributeFormat(attribute),
                    Values = Flatten(values, dimension)
                });
            }

            VertexAttribute[] supportedAttributes =
            {
                VertexAttribute.Position,
                VertexAttribute.Normal,
                VertexAttribute.Tangent,
                VertexAttribute.Color,
                VertexAttribute.TexCoord0,
                VertexAttribute.TexCoord1,
                VertexAttribute.TexCoord2,
                VertexAttribute.TexCoord3,
                VertexAttribute.TexCoord4,
                VertexAttribute.TexCoord5,
                VertexAttribute.TexCoord6,
                VertexAttribute.TexCoord7,
                VertexAttribute.BlendIndices,
                VertexAttribute.BlendWeight
            };
            foreach (VertexAttribute attribute in Enum.GetValues(typeof(VertexAttribute)))
            {
                if (source.HasVertexAttribute(attribute) && !supportedAttributes.Contains(attribute))
                    throw new InvalidDataException(
                        $"Mesh '{hierarchyPath}' has unsupported vertex attribute '{attribute}'; refusing to drop it.");
            }

            return attributes;
        }

        private static void AddVector3Attribute(
            Mesh source,
            List<AttributeData> attributes,
            VertexAttribute attribute,
            int vertexCount,
            string hierarchyPath)
        {
            if (!source.HasVertexAttribute(attribute))
                return;
            if (source.GetVertexAttributeDimension(attribute) != 3)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has unsupported {attribute} dimension.");
            var values = new List<Vector3>();
            source.GetNormals(values);
            if (values.Count != vertexCount)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has incomplete {attribute} data.");
            attributes.Add(new AttributeData
            {
                Attribute = attribute,
                Dimension = source.GetVertexAttributeDimension(attribute),
                Stream = 0,
                Format = source.GetVertexAttributeFormat(attribute),
                Values = Flatten(values, source.GetVertexAttributeDimension(attribute))
            });
        }

        private static void AddVector4Attribute(
            Mesh source,
            List<AttributeData> attributes,
            VertexAttribute attribute,
            int vertexCount,
            string hierarchyPath)
        {
            if (!source.HasVertexAttribute(attribute))
                return;
            if (source.GetVertexAttributeDimension(attribute) != 4)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has unsupported {attribute} dimension.");
            var values = new List<Vector4>();
            source.GetTangents(values);
            if (values.Count != vertexCount)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has incomplete {attribute} data.");
            attributes.Add(new AttributeData
            {
                Attribute = attribute,
                Dimension = source.GetVertexAttributeDimension(attribute),
                Stream = 0,
                Format = source.GetVertexAttributeFormat(attribute),
                Values = Flatten(values, source.GetVertexAttributeDimension(attribute))
            });
        }

        private static void AddColorAttribute(
            Mesh source,
            List<AttributeData> attributes,
            int vertexCount,
            string hierarchyPath)
        {
            if (!source.HasVertexAttribute(VertexAttribute.Color))
                return;
            if (source.GetVertexAttributeDimension(VertexAttribute.Color) != 4)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has unsupported color dimension.");
            var values = new List<Color>();
            source.GetColors(values);
            if (values.Count != vertexCount)
                throw new InvalidDataException($"Mesh '{hierarchyPath}' has incomplete color data.");
            var flattened = new float[values.Count * 4];
            for (int i = 0; i < values.Count; i++)
            {
                flattened[i * 4] = values[i].r;
                flattened[i * 4 + 1] = values[i].g;
                flattened[i * 4 + 2] = values[i].b;
                flattened[i * 4 + 3] = values[i].a;
            }
            attributes.Add(new AttributeData
            {
                Attribute = VertexAttribute.Color,
                Dimension = source.GetVertexAttributeDimension(VertexAttribute.Color),
                Stream = 0,
                Format = source.GetVertexAttributeFormat(VertexAttribute.Color),
                Values = flattened
            });
        }

        private static void WriteVertexBuffers(Mesh mesh, List<AttributeData> attributes, int vertexCount)
        {
            var offsets = new Dictionary<VertexAttribute, int>();
            var strides = new Dictionary<int, int>();
            foreach (AttributeData attribute in attributes)
            {
                strides.TryGetValue(attribute.Stream, out int stride);
                offsets.Add(attribute.Attribute, stride);
                int componentSize = GetComponentSize(attribute.Format);
                strides[attribute.Stream] = stride + attribute.Dimension * componentSize;
            }

            var buffers = new Dictionary<int, byte[]>();
            foreach (KeyValuePair<int, int> entry in strides)
                buffers.Add(entry.Key, new byte[entry.Value * vertexCount]);

            foreach (AttributeData attribute in attributes)
            {
                int stride = strides[attribute.Stream];
                int offset = offsets[attribute.Attribute];
                int componentSize = GetComponentSize(attribute.Format);
                byte[] buffer = buffers[attribute.Stream];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    for (int component = 0; component < attribute.Dimension; component++)
                    {
                        int bufferOffset = vertex * stride + offset + component * componentSize;
                        float value = attribute.Values[vertex * attribute.Dimension + component];
                        WriteComponent(buffer, bufferOffset, attribute.Format, value);
                    }
                }
            }

            foreach (KeyValuePair<int, byte[]> entry in buffers)
                mesh.SetVertexBufferData(entry.Value, 0, 0, entry.Value.Length, entry.Key);
        }

        private static void ValidateConvertedMesh(
            Mesh source,
            Mesh converted,
            BlenderUv0Preflight.ValidationResult validation,
            string hierarchyPath)
        {
            VertexAttributeFormat expectedFormat = validation.UsesFloat32Override
                ? VertexAttributeFormat.Float32
                : VertexAttributeFormat.UNorm16;
            if (!converted.HasVertexAttribute(VertexAttribute.TexCoord0) ||
                converted.GetVertexAttributeFormat(VertexAttribute.TexCoord0) != expectedFormat ||
                converted.GetVertexAttributeDimension(VertexAttribute.TexCoord0) != 2 ||
                converted.GetVertexAttributeStream(VertexAttribute.TexCoord0) != 1)
            {
                throw new InvalidDataException(
                    $"Could not create the required UV0 layout for mesh '{hierarchyPath}'.");
            }

            ValidateGeometryStream(converted, hierarchyPath);
            if (converted.GetVertexBufferStride(1) != 4)
                throw new InvalidDataException(
                    $"Optimized mesh '{hierarchyPath}' UV0 stream is not 4 bytes per vertex.");
            if (converted.vertexCount != source.vertexCount ||
                converted.subMeshCount != source.subMeshCount ||
                converted.indexFormat != source.indexFormat ||
                converted.bounds != source.bounds)
            {
                throw new InvalidDataException(
                    $"Optimized mesh '{hierarchyPath}' changed vertex count, submesh structure, index format, or bounds.");
            }

            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                if (converted.GetTopology(submesh) != source.GetTopology(submesh) ||
                    !converted.GetIndices(submesh, false).SequenceEqual(source.GetIndices(submesh, false)))
                    throw new InvalidDataException(
                        $"Optimized mesh '{hierarchyPath}' changed topology or indices in submesh {submesh}.");
            }

            ValidateVector3Attribute(source, converted, VertexAttribute.Position, hierarchyPath);
            if (source.HasVertexAttribute(VertexAttribute.Normal))
                ValidateVector3Attribute(source, converted, VertexAttribute.Normal, hierarchyPath);
            if (source.HasVertexAttribute(VertexAttribute.Tangent))
                ValidateVector4Attribute(source, converted, VertexAttribute.Tangent, hierarchyPath);
            if (source.HasVertexAttribute(VertexAttribute.Color))
                ValidateColorAttribute(source, converted, hierarchyPath);

            var reconstructed = new List<Vector4>();
            converted.GetUVs(0, reconstructed);
            var original = new List<Vector4>();
            source.GetUVs(0, original);
            if (reconstructed.Count != original.Count)
                throw new InvalidDataException($"Optimized mesh '{hierarchyPath}' lost UV0 coordinates.");
            for (int i = 0; i < original.Count; i++)
            {
                float expectedU = validation.UsesFloat32Override
                    ? original[i].x
                    : (float)(Quantize(original[i].x) / BlenderUv0Preflight.Unorm16Maximum);
                float expectedV = validation.UsesFloat32Override
                    ? original[i].y
                    : (float)(Quantize(original[i].y) / BlenderUv0Preflight.Unorm16Maximum);
                if (!Mathf.Approximately(reconstructed[i].x, expectedU) ||
                    !Mathf.Approximately(reconstructed[i].y, expectedV))
                    throw new InvalidDataException(
                        $"Optimized mesh '{hierarchyPath}' did not preserve UV0 reconstruction at coordinate {i}.");
            }

            for (int channel = 1; channel < 8; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!source.HasVertexAttribute(attribute))
                    continue;
                var sourceValues = new List<Vector4>();
                var convertedValues = new List<Vector4>();
                source.GetUVs(channel, sourceValues);
                converted.GetUVs(channel, convertedValues);
                if (sourceValues.Count != convertedValues.Count)
                    throw new InvalidDataException(
                        $"Optimized mesh '{hierarchyPath}' changed UV channel {channel} vertex count.");
                for (int i = 0; i < sourceValues.Count; i++)
                {
                    if (sourceValues[i] != convertedValues[i])
                        throw new InvalidDataException(
                            $"Optimized mesh '{hierarchyPath}' changed UV channel {channel} at vertex {i}.");
                }
            }
        }

        private static void ValidateGeometryStream(Mesh mesh, string meshPath)
        {
            VertexAttribute[] geometryAttributes =
            {
                VertexAttribute.Position,
                VertexAttribute.Normal,
                VertexAttribute.Tangent,
                VertexAttribute.Color
            };
            foreach (VertexAttribute attribute in geometryAttributes)
            {
                if (mesh.HasVertexAttribute(attribute) &&
                    mesh.GetVertexAttributeStream(attribute) != 0)
                {
                    throw new InvalidDataException(
                        $"Mesh '{meshPath}' attribute '{attribute}' is not in geometry stream 0.");
                }
            }
        }

        private static VertexAttributeFormat[] GetUvFormats(Mesh mesh)
        {
            var formats = new VertexAttributeFormat[8];
            for (int channel = 0; channel < formats.Length; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (mesh.HasVertexAttribute(attribute))
                    formats[channel] = mesh.GetVertexAttributeFormat(attribute);
            }
            return formats;
        }

        private static string ComputeUv0Fingerprint(Mesh mesh)
        {
            var coordinates = new List<Vector4>();
            mesh.GetUVs(0, coordinates);
            var bytes = new byte[coordinates.Count * 2 * sizeof(float)];
            for (int i = 0; i < coordinates.Count; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(coordinates[i].x), 0, bytes, i * 8, sizeof(float));
                Buffer.BlockCopy(BitConverter.GetBytes(coordinates[i].y), 0, bytes, i * 8 + 4, sizeof(float));
            }
            using (MD5 hash = MD5.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        private static void ValidateVector3Attribute(
            Mesh source,
            Mesh converted,
            VertexAttribute attribute,
            string hierarchyPath)
        {
            var original = new List<Vector3>();
            var result = new List<Vector3>();
            source.GetVertices(original);
            converted.GetVertices(result);
            if (attribute == VertexAttribute.Normal)
            {
                source.GetNormals(original);
                converted.GetNormals(result);
            }
            if (!original.SequenceEqual(result))
                throw new InvalidDataException(
                    $"Optimized mesh '{hierarchyPath}' changed {attribute} values.");
        }

        private static void ValidateVector4Attribute(
            Mesh source,
            Mesh converted,
            VertexAttribute attribute,
            string hierarchyPath)
        {
            var original = new List<Vector4>();
            var result = new List<Vector4>();
            source.GetTangents(original);
            converted.GetTangents(result);
            if (!original.SequenceEqual(result))
                throw new InvalidDataException(
                    $"Optimized mesh '{hierarchyPath}' changed {attribute} values.");
        }

        private static void ValidateColorAttribute(
            Mesh source,
            Mesh converted,
            string hierarchyPath)
        {
            var original = new List<Color>();
            var result = new List<Color>();
            source.GetColors(original);
            converted.GetColors(result);
            if (!original.SequenceEqual(result))
                throw new InvalidDataException(
                    $"Optimized mesh '{hierarchyPath}' changed color values.");
        }

        private static int[] GetUvDimensions(Mesh mesh)
        {
            var dimensions = new int[8];
            for (int channel = 0; channel < dimensions.Length; channel++)
            {
                VertexAttribute attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (mesh.HasVertexAttribute(attribute))
                    dimensions[channel] = mesh.GetVertexAttributeDimension(attribute);
            }
            return dimensions;
        }

        private static double Quantize(float value)
        {
            return Math.Round(
                (double)value * BlenderUv0Preflight.Unorm16Maximum,
                MidpointRounding.ToEven);
        }

        private static int GetComponentSize(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8:
                    return 1;
                default:
                    throw new InvalidDataException($"Vertex format '{format}' is not supported by the mesh optimizer.");
            }
        }

        private static void WriteComponent(
            byte[] buffer,
            int offset,
            VertexAttributeFormat format,
            float value)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
                    return;
                case VertexAttributeFormat.Float16:
                    WriteUInt16(buffer, offset, Mathf.FloatToHalf(value));
                    return;
                case VertexAttributeFormat.UNorm8:
                    buffer[offset] = checked((byte)Math.Round(value * byte.MaxValue, MidpointRounding.ToEven));
                    return;
                case VertexAttributeFormat.SNorm8:
                    buffer[offset] = unchecked((byte)checked((sbyte)Math.Round(
                        value * sbyte.MaxValue,
                        MidpointRounding.ToEven)));
                    return;
                case VertexAttributeFormat.UNorm16:
                    WriteUInt16(
                        buffer,
                        offset,
                        checked((ushort)Math.Round(value * ushort.MaxValue, MidpointRounding.ToEven)));
                    return;
                case VertexAttributeFormat.SNorm16:
                    WriteUInt16(
                        buffer,
                        offset,
                        unchecked((ushort)checked((short)Math.Round(
                            value * short.MaxValue,
                            MidpointRounding.ToEven))));
                    return;
                case VertexAttributeFormat.UInt8:
                    buffer[offset] = checked((byte)Math.Round(value, MidpointRounding.ToEven));
                    return;
                case VertexAttributeFormat.SInt8:
                    buffer[offset] = unchecked((byte)checked((sbyte)Math.Round(value, MidpointRounding.ToEven)));
                    return;
                case VertexAttributeFormat.UInt16:
                    WriteUInt16(buffer, offset, checked((ushort)Math.Round(value, MidpointRounding.ToEven)));
                    return;
                case VertexAttributeFormat.SInt16:
                    WriteUInt16(
                        buffer,
                        offset,
                        unchecked((ushort)checked((short)Math.Round(value, MidpointRounding.ToEven))));
                    return;
                case VertexAttributeFormat.UInt32:
                    Buffer.BlockCopy(
                        BitConverter.GetBytes(checked((uint)Math.Round(value, MidpointRounding.ToEven))),
                        0,
                        buffer,
                        offset,
                        4);
                    return;
                case VertexAttributeFormat.SInt32:
                    Buffer.BlockCopy(
                        BitConverter.GetBytes(checked((int)Math.Round(value, MidpointRounding.ToEven))),
                        0,
                        buffer,
                        offset,
                        4);
                    return;
                default:
                    throw new InvalidDataException($"Vertex format '{format}' is not supported by the mesh optimizer.");
            }
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            buffer[offset] = bytes[0];
            buffer[offset + 1] = bytes[1];
        }

        private static float[] Flatten(IList<Vector3> values)
        {
            return Flatten(values, 3);
        }

        private static float[] Flatten(IList<Vector3> values, int dimension)
        {
            if (dimension < 1 || dimension > 3)
                throw new InvalidDataException($"Vertex attribute dimension {dimension} cannot be read as Vector3.");
            var result = new float[values.Count * dimension];
            for (int i = 0; i < values.Count; i++)
            {
                result[i * dimension] = values[i].x;
                if (dimension > 1) result[i * dimension + 1] = values[i].y;
                if (dimension > 2) result[i * dimension + 2] = values[i].z;
            }
            return result;
        }

        private static float[] Flatten(IList<Vector4> values, int dimension)
        {
            var result = new float[values.Count * dimension];
            for (int i = 0; i < values.Count; i++)
            {
                result[i * dimension] = values[i].x;
                if (dimension > 1) result[i * dimension + 1] = values[i].y;
                if (dimension > 2) result[i * dimension + 2] = values[i].z;
                if (dimension > 3) result[i * dimension + 3] = values[i].w;
            }
            return result;
        }

        private static string GetMeshAssetPath(string meshesFolder, string hierarchyPath)
        {
            byte[] input = Encoding.UTF8.GetBytes(hierarchyPath);
            string suffix;
            using (MD5 hash = MD5.Create())
                suffix = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty).Substring(0, 12);
            string safeName = BlenderFbxBaker.SanitizeAssetName(hierarchyPath.Replace('/', '_'));
            return meshesFolder.TrimEnd('/') + "/" + safeName + "_" + suffix + ".asset";
        }

        private static string GetHierarchyPath(Transform transform, Transform root)
        {
            var segments = new Stack<string>();
            Transform current = transform;
            while (current != null && current != root)
            {
                segments.Push(current.name + "[" + current.GetSiblingIndex() + "]");
                current = current.parent;
            }

            segments.Push(root.name);
            return string.Join("/", segments);
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            string current = "Assets";
            string[] segments = assetFolder.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, segments[i]);
                    if (string.IsNullOrEmpty(guid))
                        throw new IOException($"Could not create optimized mesh folder '{next}'.");
                }
                current = next;
            }
        }
    }

    internal sealed class MeshAssetTransaction
    {
        private sealed class Entry
        {
            public string AssetPath;
            public bool Created;
            public Mesh Backup;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private bool completed;

        public Mesh SaveMesh(string assetPath, Mesh converted)
        {
            if (completed)
                throw new InvalidOperationException("The optimized mesh transaction is already complete.");

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null ||
                    File.Exists(AbsoluteAssetPath(assetPath)))
                {
                    throw new IOException(
                        $"Optimized mesh output '{assetPath}' is occupied by an incompatible asset.");
                }

                entries.Add(new Entry { AssetPath = assetPath, Created = true });
                try
                {
                    AssetDatabase.CreateAsset(converted, assetPath);
                    Mesh created = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                    if (created == null)
                        throw new IOException($"Unity did not persist optimized mesh '{assetPath}'.");
                    return created;
                }
                catch
                {
                    if (!AssetDatabase.Contains(converted))
                        UnityEngine.Object.DestroyImmediate(converted);
                    throw;
                }
            }

            var backup = UnityEngine.Object.Instantiate(existing);
            backup.name = existing.name + "_Rollback";
            entries.Add(new Entry { AssetPath = assetPath, Backup = backup });
            try
            {
                EditorUtility.CopySerialized(converted, existing);
                existing.name = Path.GetFileNameWithoutExtension(assetPath);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(converted);
            }
        }

        public void Commit()
        {
            if (completed)
                return;
            completed = true;
            foreach (Entry entry in entries)
            {
                if (entry.Backup != null)
                    UnityEngine.Object.DestroyImmediate(entry.Backup);
            }
            entries.Clear();
        }

        public void Rollback()
        {
            if (completed)
                return;

            var errors = new List<Exception>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                try
                {
                    if (entry.Created)
                    {
                        if (AssetDatabase.LoadMainAssetAtPath(entry.AssetPath) != null &&
                            !AssetDatabase.DeleteAsset(entry.AssetPath))
                            throw new IOException($"Could not remove failed optimized mesh '{entry.AssetPath}'.");
                    }
                    else
                    {
                        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(entry.AssetPath);
                        if (existing == null)
                            throw new IOException($"Could not restore optimized mesh '{entry.AssetPath}'.");
                        EditorUtility.CopySerialized(entry.Backup, existing);
                        EditorUtility.SetDirty(existing);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
                finally
                {
                    if (entry.Backup != null)
                        UnityEngine.Object.DestroyImmediate(entry.Backup);
                }
            }

            entries.Clear();
            completed = true;
            AssetDatabase.SaveAssets();
            if (errors.Count > 0)
                throw new AggregateException("One or more optimized mesh assets could not be rolled back.", errors);
        }

        private static string AbsoluteAssetPath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }
}
