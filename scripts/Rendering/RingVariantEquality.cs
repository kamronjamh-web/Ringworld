using Godot;

namespace RingworldTechDemo.Rendering;

/// <summary>Value comparison for Godot.Variant dedup caches (objects by reference).</summary>
public static class RingVariantEquality
{
    // Godot.Variant does not override Equals (boxing ValueType.Equals; Objects/Strings never match).
    public static bool Same(in Variant a, in Variant b)
    {
        Variant.Type type = a.VariantType;
        if (type != b.VariantType) return false;
        return type switch
        {
            Variant.Type.Nil => true,
            Variant.Type.Bool => a.AsBool() == b.AsBool(),
            Variant.Type.Int => a.AsInt64() == b.AsInt64(),
            Variant.Type.Float => a.AsDouble() == b.AsDouble(),
            Variant.Type.String => a.AsString() == b.AsString(),
            Variant.Type.Vector2 => a.AsVector2() == b.AsVector2(),
            Variant.Type.Vector2I => a.AsVector2I() == b.AsVector2I(),
            Variant.Type.Rect2 => a.AsRect2() == b.AsRect2(),
            Variant.Type.Rect2I => a.AsRect2I() == b.AsRect2I(),
            Variant.Type.Vector3 => a.AsVector3() == b.AsVector3(),
            Variant.Type.Vector3I => a.AsVector3I() == b.AsVector3I(),
            Variant.Type.Transform2D => a.AsTransform2D() == b.AsTransform2D(),
            Variant.Type.Vector4 => a.AsVector4() == b.AsVector4(),
            Variant.Type.Vector4I => a.AsVector4I() == b.AsVector4I(),
            Variant.Type.Plane => a.AsPlane() == b.AsPlane(),
            Variant.Type.Quaternion => a.AsQuaternion() == b.AsQuaternion(),
            Variant.Type.Aabb => a.AsAabb() == b.AsAabb(),
            Variant.Type.Basis => a.AsBasis() == b.AsBasis(),
            Variant.Type.Transform3D => a.AsTransform3D() == b.AsTransform3D(),
            Variant.Type.Projection => a.AsProjection() == b.AsProjection(),
            Variant.Type.Color => a.AsColor() == b.AsColor(),
            Variant.Type.StringName => a.AsStringName() == b.AsStringName(),
            Variant.Type.NodePath => a.AsNodePath() == b.AsNodePath(),
            Variant.Type.Rid => a.AsRid() == b.AsRid(),
            Variant.Type.Object => ReferenceEquals(a.AsGodotObject(), b.AsGodotObject()),
            _ => false
        };
    }
}
