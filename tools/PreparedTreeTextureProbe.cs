using System;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>Returns a native texture obtained from the actual reloaded prepared artifact, not an independent PNG load.</summary>
public partial class PreparedTreeTextureProbe : RefCounted
{
    public Texture2D? LoadTexture()
    {
        try
        {
            var resource = ResourceLoader.Load<PreparedImportedTreeCatalog>(ImportedTreeCatalog.PreparedResourcePath, cacheMode: ResourceLoader.CacheMode.IgnoreDeep)
                ?? throw new InvalidOperationException("prepared catalog missing");
            string? source = OS.HasFeature("editor")
                ? PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(ProjectSettings.GlobalizePath("res://")).Fingerprint : null;
            var variants = PreparedImportedTreeCatalogValidation.Hydrate(resource, PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity,
                PreparedImportedTreeCatalogIdentity.ImplementationIdentity, source);
            foreach (ShaderMaterial material in variants[0].Materials)
            {
                Variant value = material.GetShaderParameter("albedo_texture");
                if (value.VariantType == Variant.Type.Object && value.AsGodotObject() is Texture2D texture
                    && texture.GetClass() == "StreamedTexture2D") return texture;
            }
            throw new InvalidOperationException("reloaded Maple materials lost their native streamed bark texture");
        }
        catch (Exception exception)
        {
            GD.PushError("PREPARED_TREE_TEXTURE_SOURCE=FAIL " + exception);
            return null;
        }
    }
}
