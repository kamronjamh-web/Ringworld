using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>A shaded compass needle showing wind destination relative to character yaw.</summary>
public partial class RingMinimapWindArrow : Control
{
    public Vector2 ScreenDirection { get; private set; }
    public Vector2 FacingRelativeDirection { get; private set; }
    public bool HasDirection { get; private set; }

    public RingMinimapWindArrow() => MouseFilter = MouseFilterEnum.Ignore;

    public static Vector2 ResolveFacingRelativeWind(Vector2 wind, float yaw)
    {
        if (!wind.IsFinite() || !float.IsFinite(yaw)) return Vector2.Zero;
        // Canonical gameplay yaw: zero faces +around, with right toward -across.
        // Only yaw participates: looking up/down or rolling the view never turns the compass.
        Vector2 forward = new(Mathf.Cos(yaw), Mathf.Sin(yaw));
        Vector2 right = new(forward.Y, -forward.X);
        return new Vector2(wind.Dot(right), -wind.Dot(forward));
    }

    public void SetDirection(Vector2 direction, bool available)
    {
        bool hasDirection = available && direction.IsFinite() && direction.LengthSquared() > 0.000001f;
        Vector2 normalized = hasDirection ? direction.Normalized() : Vector2.Zero;
        if (HasDirection == hasDirection && FacingRelativeDirection == normalized) return;
        HasDirection = hasDirection;
        FacingRelativeDirection = normalized;
        ScreenDirection = hasDirection ? new Vector2(normalized.X, normalized.Y * 0.62f).Normalized() : Vector2.Zero;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 center = Size * 0.5f + new Vector2(0, 3);
        Color rim = new(0.29f, 0.49f, 0.60f);
        // A fixed oblique compass deck makes the pointer read as a raised 3D needle.
        const int segments = 32;
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.Tau / segments;
            float b = (i + 1) * Mathf.Tau / segments;
            DrawLine(center + new Vector2(Mathf.Cos(a) * 22, Mathf.Sin(a) * 13.6f),
                center + new Vector2(Mathf.Cos(b) * 22, Mathf.Sin(b) * 13.6f), rim, 1, true);
        }
        // The bright forward tick identifies character-facing, not geographic north.
        DrawLine(center + new Vector2(-3, -17), center + new Vector2(0, -20), rim.Lightened(.35f), 1.5f, true);
        DrawLine(center + new Vector2(0, -20), center + new Vector2(3, -17), rim.Lightened(.35f), 1.5f, true);
        if (!HasDirection) return;
        Vector2 direction = FacingRelativeDirection;
        Basis rotation = new(Vector3.Up, Mathf.Atan2(-direction.X, -direction.Y));
        Vector3 tip = rotation * new Vector3(0, 1, -18);
        Vector3 tail = rotation * new Vector3(0, 1, 12);
        Vector3 left = rotation * new Vector3(-6, 1, 0);
        Vector3 right = rotation * new Vector3(6, 1, 0);
        Vector3 ridge = new(0, 5, 0);
        DrawFace(center, tail, left, ridge, new Color(.34f, .52f, .63f));
        DrawFace(center, tail, ridge, right, new Color(.34f, .52f, .63f));
        DrawFace(center, tip, ridge, left, new Color(.60f, .91f, 1.0f));
        DrawFace(center, tip, right, ridge, new Color(.60f, .91f, 1.0f));
        DrawLine(Project(center, tail), Project(center, ridge), new Color(.42f, .65f, .76f), 1, true);
        DrawLine(Project(center, ridge), Project(center, tip), new Color(.83f, .98f, 1), 1, true);
    }

    private void DrawFace(Vector2 center, Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        Vector3 normal = (b - a).Cross(c - a).Normalized();
        if (normal.Y < 0) normal = -normal;
        float light = .40f + .60f * Mathf.Max(0, normal.Dot(new Vector3(-.45f, .8f, -.35f).Normalized()));
        DrawColoredPolygon(new[] { Project(center, a), Project(center, b), Project(center, c) },
            new Color(color.R * light, color.G * light, color.B * light, color.A));
    }

    private static Vector2 Project(Vector2 center, Vector3 point)
        => center + new Vector2(point.X, point.Z * 0.62f - point.Y * 0.72f);
}
