using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FortnitePorting.Models.Uefn;

/// <summary>
/// Rewrites copied glTF payloads so the Import Textures / Import Materials toggles are real:
/// when a toggle is off, the corresponding data is removed from the copy that UEFN imports.
/// The exported source files are never modified.
/// </summary>
public static class UefnGltfProcessor
{
    private const uint GlbMagic = 0x46546C67; // 'glTF'
    private const uint JsonChunkType = 0x4E4F534A; // 'JSON'
    private const uint BinaryChunkType = 0x004E4942; // 'BIN\0'
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static bool WriteProcessed(string sourcePath, string destinationPath, bool removeTextures, bool removeMaterials)
    {
        if (!removeTextures && !removeMaterials)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return false;
        }

        if (Path.GetExtension(sourcePath).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(sourcePath);
            var rewritten = ProcessGltfJson(text, removeTextures, removeMaterials);
            File.WriteAllText(destinationPath, rewritten, new UTF8Encoding(false));
            return true;
        }

        var glb = File.ReadAllBytes(sourcePath);
        var output = ProcessGlb(glb, removeTextures, removeMaterials);
        File.WriteAllBytes(destinationPath, output);
        return true;
    }

    public static byte[] ProcessGlb(byte[] glb, bool removeTextures, bool removeMaterials)
    {
        if (glb.Length < 20)
            throw new InvalidDataException("The GLB file is incomplete (no glTF header).");

        using var input = new MemoryStream(glb, writable: false);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();
        var declaredLength = reader.ReadUInt32();
        if (magic != GlbMagic || version != 2)
            throw new InvalidDataException("The file is not a glTF 2.0 binary (.glb).");
        if (declaredLength > glb.Length || declaredLength < 20)
            throw new InvalidDataException("The GLB file is incomplete (invalid header length). UEFN cannot import a partially written export.");

        var chunks = new List<(uint Type, byte[] Data)>();
        while (input.Position + 8 <= declaredLength)
        {
            var chunkLength = reader.ReadUInt32();
            var chunkType = reader.ReadUInt32();
            if (chunkLength > int.MaxValue || input.Position + chunkLength > declaredLength)
                throw new InvalidDataException("The GLB file is incomplete (truncated chunk).");

            chunks.Add((chunkType, reader.ReadBytes((int) chunkLength)));
        }

        var jsonChunkIndex = chunks.FindIndex(chunk => chunk.Type == JsonChunkType);
        if (jsonChunkIndex < 0)
            throw new InvalidDataException("The GLB file has no JSON chunk.");

        var jsonText = Encoding.UTF8.GetString(chunks[jsonChunkIndex].Data).TrimEnd('\0', ' ');
        var root = JsonNode.Parse(jsonText) as JsonObject
                   ?? throw new InvalidDataException("The GLB JSON chunk is invalid.");

        var changed = Modify(root, removeTextures, removeMaterials);
        if (!changed)
            return glb;

        var jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString(CompactJson));
        var originalLength = jsonBytes.Length;
        var paddedLength = (originalLength + 3) & ~3;
        Array.Resize(ref jsonBytes, paddedLength);
        for (var index = originalLength; index < paddedLength; index++)
            jsonBytes[index] = 0x20;

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(GlbMagic);
        writer.Write(2u);
        writer.Write(0u); // patched below
        writer.Write((uint) jsonBytes.Length);
        writer.Write(JsonChunkType);
        writer.Write(jsonBytes);

        for (var index = 0; index < chunks.Count; index++)
        {
            if (index == jsonChunkIndex) continue;
            writer.Write((uint) chunks[index].Data.Length);
            writer.Write(chunks[index].Type);
            writer.Write(chunks[index].Data);
        }

        writer.Flush();
        var totalLength = (uint) output.Length;
        output.Position = 8;
        writer.Write(totalLength);
        writer.Flush();

        return output.ToArray();
    }

    public static string ProcessGltfJson(string text, bool removeTextures, bool removeMaterials)
    {
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("The .gltf file contains invalid JSON.");

        if (!Modify(root, removeTextures, removeMaterials))
            return text;

        return root.ToJsonString(CompactJson);
    }

    private static bool Modify(JsonObject root, bool removeTextures, bool removeMaterials)
    {
        var changed = false;

        if (removeMaterials)
        {
            if (root.Remove("materials")) changed = true;

            if (root["meshes"] is JsonArray meshes)
            {
                foreach (var meshNode in meshes)
                {
                    if (meshNode is not JsonObject mesh || mesh["primitives"] is not JsonArray primitives)
                        continue;

                    foreach (var primitiveNode in primitives)
                    {
                        if (primitiveNode is not JsonObject primitive) continue;

                        if (primitive.Remove("material")) changed = true;

                        if (primitive["extensions"] is JsonObject extensions &&
                            extensions.Remove("KHR_materials_variants"))
                            changed = true;
                    }
                }
            }
        }

        if (removeTextures)
        {
            if (root.Remove("textures")) changed = true;
            if (root.Remove("images")) changed = true;
            if (root.Remove("samplers")) changed = true;

            if (root["materials"] is JsonArray materials)
            {
                foreach (var materialNode in materials)
                {
                    if (materialNode is JsonObject material &&
                        RemoveTextureReferences(material))
                        changed = true;
                }
            }
        }

        return changed;
    }

    private static bool RemoveTextureReferences(JsonObject node)
    {
        var changed = false;
        foreach (var key in node.Select(pair => pair.Key).ToArray())
        {
            var value = node[key];

            if (key.Equals("texture", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("Texture", StringComparison.OrdinalIgnoreCase))
            {
                node.Remove(key);
                changed = true;
                continue;
            }

            if (value is JsonObject child)
            {
                changed |= RemoveTextureReferences(child);
            }
            else if (value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject itemObject)
                        changed |= RemoveTextureReferences(itemObject);
                }
            }
        }

        return changed;
    }
}
