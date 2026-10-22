using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BlenderBridge.Editor
{
    internal static class BlenderUv0Preflight
    {
        internal const int Unorm16Maximum = 65535;
        internal const double MaximumAllowedTexelError = 0.25;

        internal sealed class TextureResolution
        {
            public TextureResolution(string description, int width, int height)
            {
                Description = description;
                Width = width;
                Height = height;
            }

            public string Description { get; }
            public int Width { get; }
            public int Height { get; }
        }

        internal sealed class ValidationResult
        {
            public ValidationResult(
                string meshContext,
                double maximumTexelError,
                TextureResolution limitingResolution,
                int limitingCoordinateIndex,
                Vector2 limitingCoordinate,
                bool usesFloat32Override)
            {
                MeshContext = meshContext;
                MaximumTexelError = maximumTexelError;
                LimitingResolution = limitingResolution;
                LimitingCoordinateIndex = limitingCoordinateIndex;
                LimitingCoordinate = limitingCoordinate;
                UsesFloat32Override = usesFloat32Override;
            }

            public string MeshContext { get; }
            public double MaximumTexelError { get; }
            public TextureResolution LimitingResolution { get; }
            public int LimitingCoordinateIndex { get; }
            public Vector2 LimitingCoordinate { get; }
            public bool UsesFloat32Override { get; }
            public string SelectedFormat => UsesFloat32Override ? "Float32 (explicit override)" : "UNorm16";
        }

        internal static List<ValidationResult> ValidateModel(
            string sourceAssetPath,
            GameObject model,
            int maximumTextureDimension,
            bool allowFloat32Override)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (maximumTextureDimension <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maximumTextureDimension),
                    "The maximum UV validation texture dimension must be positive.");

            var results = new List<ValidationResult>();
            var coordinates = new List<Vector4>();
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter == null ? null : filter.sharedMesh;
                string meshContext = $"{sourceAssetPath}: {GetHierarchyPath(renderer.transform, model.transform)}";
                if (mesh == null)
                    throw new InvalidDataException($"FBX mesh '{meshContext}' has no imported mesh for UV0 validation.");

                coordinates.Clear();
                mesh.GetUVs(0, coordinates);
                if (coordinates.Count == 0)
                {
                    throw new InvalidDataException(
                        $"FBX mesh '{meshContext}' has no UV0 channel (Unity UV layer 'UV0'). UV0 is required for the bake.");
                }
                if (mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0) != 2)
                {
                    throw new InvalidDataException(
                        $"FBX mesh '{meshContext}' UV0 has dimension " +
                        $"{mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0)}; " +
                        "the bake requires a two-component UV0 channel.");
                }
                if (coordinates.Count != mesh.vertexCount)
                {
                    throw new InvalidDataException(
                        $"FBX mesh '{meshContext}' UV0 channel has {coordinates.Count} coordinates for {mesh.vertexCount} vertices.");
                }

                var resolutions = new List<TextureResolution>
                {
                    new TextureResolution(
                        "configured maximum",
                        maximumTextureDimension,
                        maximumTextureDimension)
                };
                AddMaterialTextureResolutions(renderer, resolutions);
                results.Add(ValidateCoordinates(
                    meshContext,
                    "UV0",
                    coordinates,
                    resolutions,
                    allowFloat32Override));
            }

            return results;
        }

        internal static ValidationResult ValidateCoordinates(
            string meshContext,
            string uvLayerName,
            IList<Vector4> coordinates,
            IList<TextureResolution> resolutions,
            bool allowFloat32Override)
        {
            if (coordinates == null)
                throw new ArgumentNullException(nameof(coordinates));
            if (coordinates.Count == 0)
                throw new InvalidDataException(
                    $"FBX mesh '{meshContext}' UV layer '{uvLayerName}' contains no coordinates.");
            if (resolutions == null || resolutions.Count == 0)
                throw new ArgumentException("At least one texture resolution is required.", nameof(resolutions));

            var validResolutions = new List<TextureResolution>(resolutions.Count);
            for (int i = 0; i < resolutions.Count; i++)
            {
                TextureResolution resolution = resolutions[i];
                if (resolution == null || resolution.Width <= 0 || resolution.Height <= 0)
                {
                    throw new ArgumentException(
                        $"Texture resolution at index {i} must have positive dimensions.",
                        nameof(resolutions));
                }
                validResolutions.Add(resolution);
            }

            double maximumTexelError = 0d;
            TextureResolution limitingResolution = validResolutions[0];
            int limitingCoordinateIndex = 0;
            Vector2 limitingCoordinate = new Vector2(coordinates[0].x, coordinates[0].y);
            for (int i = 0; i < coordinates.Count; i++)
            {
                Vector4 coordinate = coordinates[i];
                ValidateComponent(meshContext, uvLayerName, i, "U", coordinate.x);
                ValidateComponent(meshContext, uvLayerName, i, "V", coordinate.y);

                double reconstructedU = Reconstruct(coordinate.x);
                double reconstructedV = Reconstruct(coordinate.y);
                for (int resolutionIndex = 0; resolutionIndex < validResolutions.Count; resolutionIndex++)
                {
                    TextureResolution resolution = validResolutions[resolutionIndex];
                    double error = Math.Max(
                        Math.Abs((double)coordinate.x - reconstructedU) * resolution.Width,
                        Math.Abs((double)coordinate.y - reconstructedV) * resolution.Height);
                    if (error > maximumTexelError)
                    {
                        maximumTexelError = error;
                        limitingResolution = resolution;
                        limitingCoordinateIndex = i;
                        limitingCoordinate = new Vector2(coordinate.x, coordinate.y);
                    }
                }
            }

            bool exceedsTolerance = maximumTexelError > MaximumAllowedTexelError;
            if (exceedsTolerance && !allowFloat32Override)
            {
                throw new InvalidDataException(
                    $"FBX mesh '{meshContext}' UV layer '{uvLayerName}' exceeds the UNorm16 error limit: " +
                    $"coordinate {limitingCoordinateIndex} ({limitingCoordinate.x:R}, {limitingCoordinate.y:R}) " +
                    $"has maximum error {maximumTexelError:F6} texel at {limitingResolution.Width}x{limitingResolution.Height} " +
                    $"({limitingResolution.Description}); allowed {MaximumAllowedTexelError:F2} texel. " +
                    "Select the explicit project Float32 UV0 override before baking to allow this mesh.");
            }

            return new ValidationResult(
                meshContext,
                maximumTexelError,
                limitingResolution,
                limitingCoordinateIndex,
                limitingCoordinate,
                exceedsTolerance && allowFloat32Override);
        }

        private static double Reconstruct(float value)
        {
            double quantized = Math.Round(
                (double)value * Unorm16Maximum,
                MidpointRounding.ToEven);
            return quantized / Unorm16Maximum;
        }

        private static void ValidateComponent(
            string meshContext,
            string uvLayerName,
            int coordinateIndex,
            string axis,
            float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidDataException(
                    $"FBX mesh '{meshContext}' UV layer '{uvLayerName}' has a non-finite {axis} value " +
                    $"at coordinate {coordinateIndex}: {value}.");
            }

            if (value < 0f || value > 1f)
            {
                throw new InvalidDataException(
                    $"FBX mesh '{meshContext}' UV layer '{uvLayerName}' has an out-of-range {axis} value " +
                    $"at coordinate {coordinateIndex}: {value}; expected [0,1]. Values are not clamped.");
            }
        }

        private static void AddMaterialTextureResolutions(
            MeshRenderer renderer,
            List<TextureResolution> resolutions)
        {
            Material[] materials = renderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material material = materials[materialIndex];
                if (material == null)
                    continue;

                string[] textureProperties = material.GetTexturePropertyNames();
                for (int propertyIndex = 0; propertyIndex < textureProperties.Length; propertyIndex++)
                {
                    Texture texture = material.GetTexture(textureProperties[propertyIndex]);
                    if (texture == null)
                        continue;

                    string assetPath = UnityEditor.AssetDatabase.GetAssetPath(texture);
                    string description = string.IsNullOrEmpty(assetPath)
                        ? $"texture '{texture.name}'"
                        : $"texture '{assetPath}'";
                    resolutions.Add(new TextureResolution(description, texture.width, texture.height));
                }
            }
        }

        private static string GetHierarchyPath(Transform transform, Transform root)
        {
            var segments = new Stack<string>();
            Transform current = transform;
            while (current != null && current != root)
            {
                segments.Push(current.name);
                current = current.parent;
            }

            segments.Push(root.name);
            return string.Join("/", segments);
        }
    }
}
