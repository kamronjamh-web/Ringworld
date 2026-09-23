using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

public partial class VoxelTestExplorerController : CharacterBody3D
{
    [Export] public NodePath HeadPath { get; set; } = new NodePath("Head");
    [Export] public NodePath CameraPath { get; set; } = new NodePath("Head/Camera3D");

    [Export(PropertyHint.Range, "1,30,0.1")] public float MoveSpeed { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "1,60,0.1")] public float SprintSpeed { get; set; } = 14.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float GroundAcceleration { get; set; } = 30.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float AirAcceleration { get; set; } = 10.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float Gravity { get; set; } = 24.0f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float JumpVelocity { get; set; } = 8.5f;
    [Export(PropertyHint.Range, "1,120,0.1")] public float NoclipMoveSpeed { get; set; } = 18.0f;
    [Export(PropertyHint.Range, "1,6,0.1")] public float NoclipSprintMultiplier { get; set; } = 2.0f;
    [Export(PropertyHint.Range, "0.01,1.0,0.01")] public float MouseSensitivity { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "-89,0,0.1")] public float MinPitchDegrees { get; set; } = -85.0f;
    [Export(PropertyHint.Range, "0,89,0.1")] public float MaxPitchDegrees { get; set; } = 85.0f;

    private Node3D? _head;
    private Camera3D? _camera;
    private float _pitchDegrees;
    private bool _isNoclipEnabled;

    public override void _Ready()
    {
        _head = GetNodeOrNull<Node3D>(HeadPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);

        if (_head != null)
        {
            _pitchDegrees = _head.RotationDegrees.X;
        }

        CaptureMouse();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(Mathf.DegToRad(-mouseMotion.Relative.X * MouseSensitivity));

            if (_head != null)
            {
                _pitchDegrees = Mathf.Clamp(
                    _pitchDegrees - mouseMotion.Relative.Y * MouseSensitivity,
                    MinPitchDegrees,
                    MaxPitchDegrees);

                Vector3 headRot = _head.RotationDegrees;
                headRot.X = _pitchDegrees;
                _head.RotationDegrees = headRot;
            }

            return;
        }

        if (@event is InputEventKey keyEvent
            && keyEvent.Pressed
            && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.V)
            {
                _isNoclipEnabled = !_isNoclipEnabled;
                Velocity = Vector3.Zero;
                GD.Print($"Voxel explorer noclip: {(_isNoclipEnabled ? "ON" : "OFF")}");
                return;
            }

            if (keyEvent.Keycode == Key.Escape)
            {
                Input.MouseMode = Input.MouseModeEnum.Visible;
                return;
            }

            return;
        }

        if (@event is InputEventMouseButton mouseButton
            && mouseButton.Pressed
            && Input.MouseMode != Input.MouseModeEnum.Captured
            && !GetTree().Root.GetMeta(global::RingworldTechDemo.UI.RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool())
        {
            CaptureMouse();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        if (_isNoclipEnabled)
        {
            ProcessNoclipFlight(dt);
            return;
        }

        Vector3 velocity = Velocity;

        bool jumpPressed = Input.IsPhysicalKeyPressed(Key.Space);
        if (IsOnFloor())
        {
            if (jumpPressed)
            {
                velocity.Y = JumpVelocity;
            }
            else if (velocity.Y < 0.0f)
            {
                velocity.Y = 0.0f;
            }
        }
        else
        {
            velocity.Y -= Gravity * dt;
        }

        float inputX = 0.0f;
        float inputZ = 0.0f;

        if (Input.IsPhysicalKeyPressed(Key.A))
        {
            inputX -= 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.D))
        {
            inputX += 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.W))
        {
            inputZ += 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.S))
        {
            inputZ -= 1.0f;
        }

        Vector2 moveInput = new Vector2(inputX, inputZ);
        if (moveInput.LengthSquared() > 1.0f)
        {
            moveInput = moveInput.Normalized();
        }

        Vector3 forward = -GlobalTransform.Basis.Z;
        forward.Y = 0.0f;
        forward = forward.LengthSquared() > 0.0001f ? forward.Normalized() : Vector3.Forward;

        Vector3 right = GlobalTransform.Basis.X;
        right.Y = 0.0f;
        right = right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Right;

        Vector3 desiredDirection = (forward * moveInput.Y) + (right * moveInput.X);
        if (desiredDirection.LengthSquared() > 1.0f)
        {
            desiredDirection = desiredDirection.Normalized();
        }

        float targetSpeed = Input.IsPhysicalKeyPressed(Key.Shift) ? SprintSpeed : MoveSpeed;

        Vector3 horizontalVelocity = new Vector3(velocity.X, 0.0f, velocity.Z);
        Vector3 targetHorizontalVelocity = desiredDirection * targetSpeed;
        float acceleration = IsOnFloor() ? GroundAcceleration : AirAcceleration;
        horizontalVelocity = horizontalVelocity.MoveToward(targetHorizontalVelocity, acceleration * dt);

        velocity.X = horizontalVelocity.X;
        velocity.Z = horizontalVelocity.Z;

        Velocity = velocity;
        MoveAndSlide();
    }

    private void ProcessNoclipFlight(float dt)
    {
        Basis moveBasis = _camera?.GlobalTransform.Basis ?? GlobalTransform.Basis;

        Vector3 forward = -moveBasis.Z;
        forward = forward.LengthSquared() > 0.0001f ? forward.Normalized() : Vector3.Forward;

        Vector3 right = moveBasis.X;
        right = right.LengthSquared() > 0.0001f ? right.Normalized() : Vector3.Right;

        Vector3 up = moveBasis.Y;
        up = up.LengthSquared() > 0.0001f ? up.Normalized() : Vector3.Up;

        float inputForward = 0.0f;
        float inputRight = 0.0f;
        float inputUp = 0.0f;

        if (Input.IsPhysicalKeyPressed(Key.W))
        {
            inputForward += 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.S))
        {
            inputForward -= 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.D))
        {
            inputRight += 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.A))
        {
            inputRight -= 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.Space) || Input.IsPhysicalKeyPressed(Key.E))
        {
            inputUp += 1.0f;
        }

        if (Input.IsPhysicalKeyPressed(Key.C) || Input.IsPhysicalKeyPressed(Key.Q))
        {
            inputUp -= 1.0f;
        }

        Vector3 moveDirection = (forward * inputForward) + (right * inputRight) + (up * inputUp);
        if (moveDirection.LengthSquared() > 1.0f)
        {
            moveDirection = moveDirection.Normalized();
        }

        float speed = NoclipMoveSpeed;
        if (Input.IsPhysicalKeyPressed(Key.Shift))
        {
            speed *= Mathf.Max(1.0f, NoclipSprintMultiplier);
        }

        GlobalPosition += moveDirection * speed * dt;
        Velocity = Vector3.Zero;
    }

    private static void CaptureMouse()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }
}
