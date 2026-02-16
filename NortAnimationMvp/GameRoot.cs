using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NortAnimationMvp.Animation;
using NortAnimationMvp.Assets;
using NortAnimationMvp.Rendering;

namespace NortAnimationMvp;

public sealed class GameRoot : Game
{
    private const bool UseAutomaticModelCulling = true;
    private const CullMode ManualModelCullMode = CullMode.CullCounterClockwiseFace;

    private readonly GraphicsDeviceManager _graphics;

    private Animator? _animator;
    private SkinnedMeshRenderer? _renderer;
    private GroundGridRenderer? _groundGrid;

    private Matrix _world = Matrix.Identity;
    private Matrix _view;
    private Matrix _projection;
    private RasterizerState _modelRasterizerState = RasterizerState.CullCounterClockwise;

    private KeyboardState _previousKeyboard;
    private int _previousScrollWheelValue;

    private Vector3 _cameraPosition = new(0f, 1.25f, 3.5f);
    private float _cameraYaw = MathF.PI;
    private float _cameraPitch = -0.05f;
    private float _cameraFovDegrees = 60f;
    private bool _isMaximizedWindow;

    private const float CameraMoveSpeed = 2.8f;
    private const float CameraFastMultiplier = 2.5f;
    private const float MouseLookSensitivity = 0.003f;
    private const float KeyboardTurnSpeed = 1.9f;
    private const float ZoomStepDegrees = 1.2f;

    public GameRoot()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true
        };

        IsMouseVisible = false;
        Window.Title = "MonoGame Mixamo MVP - 1:Idle 2:Walk 3:Run 4:Bash | WASD+Mouse+Scroll | F10 Max/Restore | F11 Fullscreen";
    }

    protected override void Initialize()
    {
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += (_, _) => RebuildProjectionMatrix();

        LookAtWorldTarget(Vector3.Zero);
        RebuildViewMatrix();
        RebuildProjectionMatrix();

        _previousScrollWheelValue = Mouse.GetState().ScrollWheelValue;
        CenterMouseInWindow();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        var modelsDir = ResolveModelsDirectory();
        var loader = new GlbRuntimeLoader();
        var modelPath = Path.Combine(modelsDir, "/Users/rui/src/pg/m2g/output/maria_wprop_j_k_ong.glb");
        var animationSet = loader.Load(
            GraphicsDevice,
            modelPath,
            new Dictionary<string, string>
            {
                ["idle"] = Path.Combine(modelsDir, "idle.glb"),
                ["walk"] = Path.Combine(modelsDir, "walking.glb"),
                ["run"] = Path.Combine(modelsDir, "running.glb"),
                ["bash"] = Path.Combine(modelsDir, "punching.glb"),
                ["catwalk_walk_forward"] = Path.Combine(modelsDir, "catwalk_walk_forward.glb"),
                ["dying"] = Path.Combine(modelsDir, "dying.glb"),
                ["look_around"] = Path.Combine(modelsDir, "look_around.glb"),
                ["ninja_idle"] = Path.Combine(modelsDir, "ninja_idle.glb"),
                ["offensive_idle"] = Path.Combine(modelsDir, "offensive_idle.glb")
            });
        
        _animator = new Animator(animationSet.Model.Skeleton, animationSet.Clips);
        _animator.Play("idle", immediate: true);

        _modelRasterizerState = ResolveModelRasterizerState(animationSet.Model);
        _groundGrid = new GroundGridRenderer(GraphicsDevice, halfExtentMeters: 25, spacingMeters: 1f);
        _renderer = new SkinnedMeshRenderer(GraphicsDevice, animationSet.Model);
    }

    protected override void Update(GameTime gameTime)
    {
        if (_animator is null)
        {
            return;
        }

        var deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
        var keyboard = Keyboard.GetState();
        var mouse = Mouse.GetState();

        if (keyboard.IsKeyDown(Keys.Escape))
        {
            Exit();
        }

        if (keyboard.IsKeyDown(Keys.F11) && !_previousKeyboard.IsKeyDown(Keys.F11))
        {
            _graphics.ToggleFullScreen();
            RebuildProjectionMatrix();
        }

        if (keyboard.IsKeyDown(Keys.F10) && !_previousKeyboard.IsKeyDown(Keys.F10))
        {
            _isMaximizedWindow = !_isMaximizedWindow;
            if (_isMaximizedWindow)
            {
                _graphics.PreferredBackBufferWidth = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
                _graphics.PreferredBackBufferHeight = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;
                _graphics.ApplyChanges();
            }
            else
            {
                _graphics.PreferredBackBufferWidth = 1280;
                _graphics.PreferredBackBufferHeight = 720;
                _graphics.ApplyChanges();
            }

            RebuildProjectionMatrix();
        }

        TriggerTransition(keyboard, Keys.D1, "idle");
        TriggerTransition(keyboard, Keys.D2, "walk");
        TriggerTransition(keyboard, Keys.D3, "run");
        TriggerTransition(keyboard, Keys.D4, "bash");
        TriggerTransition(keyboard, Keys.D5, "catwalk_walk_forward");
        TriggerTransition(keyboard, Keys.D6, "dying");
        TriggerTransition(keyboard, Keys.D7, "look_around");
        TriggerTransition(keyboard, Keys.D8, "ninja_idle");
        TriggerTransition(keyboard, Keys.D9, "offensive_idle");



        UpdateCamera(deltaTime, keyboard, mouse);
        _animator.Update(deltaTime);

        _previousKeyboard = keyboard;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_animator is null || _renderer is null)
        {
            return;
        }

        GraphicsDevice.Clear(Color.CornflowerBlue);
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = _modelRasterizerState;

        _groundGrid?.Draw(_view, _projection);
        _renderer.Draw(_world, _view, _projection, _animator.SkinTransforms);

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _groundGrid?.Dispose();
        _renderer?.Dispose();
        base.UnloadContent();
    }

    private void TriggerTransition(KeyboardState keyboard, Keys key, string clip)
    {
        if (_animator is null)
        {
            return;
        }

        if (keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key))
        {
            _animator.CrossFade(clip, 0.2f);
        }
    }

    private void UpdateCamera(float deltaTime, KeyboardState keyboard, MouseState mouse)
    {
        var centerX = Window.ClientBounds.Width / 2;
        var centerY = Window.ClientBounds.Height / 2;

        var deltaX = IsActive ? mouse.X - centerX : 0;
        var deltaY = IsActive ? mouse.Y - centerY : 0;

        _cameraYaw -= deltaX * MouseLookSensitivity;
        _cameraPitch -= deltaY * MouseLookSensitivity;
        _cameraPitch = MathHelper.Clamp(_cameraPitch, -1.45f, 1.45f);

        if (keyboard.IsKeyDown(Keys.A))
        {
            _cameraYaw += KeyboardTurnSpeed * deltaTime;
        }

        if (keyboard.IsKeyDown(Keys.D))
        {
            _cameraYaw -= KeyboardTurnSpeed * deltaTime;
        }

        var scrollDelta = mouse.ScrollWheelValue - _previousScrollWheelValue;
        _previousScrollWheelValue = mouse.ScrollWheelValue;
        if (scrollDelta != 0)
        {
            _cameraFovDegrees -= (scrollDelta / 120f) * ZoomStepDegrees;
            _cameraFovDegrees = MathHelper.Clamp(_cameraFovDegrees, 25f, 90f);
            RebuildProjectionMatrix();
        }

        var movementRotation = Matrix.CreateFromYawPitchRoll(_cameraYaw, _cameraPitch, 0f);
        var cameraForward3D = Vector3.Transform(Vector3.Forward, movementRotation);
        var flatForward = new Vector3(cameraForward3D.X, 0f, cameraForward3D.Z);
        if (flatForward.LengthSquared() < 0.000001f)
        {
            flatForward = Vector3.Forward;
        }
        else
        {
            flatForward.Normalize();
        }

        var flatRight = Vector3.Normalize(Vector3.Cross(flatForward, Vector3.Up));

        var move = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) move += flatForward;
        if (keyboard.IsKeyDown(Keys.S)) move -= flatForward;
        if (keyboard.IsKeyDown(Keys.Q)) move -= flatRight;
        if (keyboard.IsKeyDown(Keys.E)) move += flatRight;

        if (move != Vector3.Zero)
        {
            move.Normalize();
            var speed = CameraMoveSpeed * (keyboard.IsKeyDown(Keys.LeftShift) ? CameraFastMultiplier : 1f);
            _cameraPosition += move * speed * deltaTime;
        }

        if (IsActive)
        {
            Mouse.SetPosition(centerX, centerY);
        }

        RebuildViewMatrix();
    }

    private void CenterMouseInWindow()
    {
        Mouse.SetPosition(Window.ClientBounds.Width / 2, Window.ClientBounds.Height / 2);
    }

    private void RebuildViewMatrix()
    {
        var rotation = Matrix.CreateFromYawPitchRoll(_cameraYaw, _cameraPitch, 0f);
        var forward = Vector3.Transform(Vector3.Forward, rotation);
        var up = Vector3.Transform(Vector3.Up, rotation);
        _view = Matrix.CreateLookAt(_cameraPosition, _cameraPosition + forward, up);
    }

    private void LookAtWorldTarget(Vector3 target)
    {
        var direction = target - _cameraPosition;
        if (direction.LengthSquared() < 0.000001f)
        {
            return;
        }

        direction.Normalize();
        _cameraYaw = MathF.Atan2(direction.X, -direction.Z);
        _cameraPitch = MathF.Asin(MathHelper.Clamp(direction.Y, -1f, 1f));
        _cameraPitch = MathHelper.Clamp(_cameraPitch, -1.45f, 1.45f);
    }

    private void RebuildProjectionMatrix()
    {
        _projection = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(_cameraFovDegrees),
            GraphicsDevice.Viewport.AspectRatio,
            0.01f,
            100f);
    }

    private static string ResolveModelsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "../../../../glb_models"),
            Path.Combine(Directory.GetCurrentDirectory(), "glb_models")
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new DirectoryNotFoundException("Não encontrei o diretório mixamo_models.");
    }

    private RasterizerState ResolveModelRasterizerState(Runtime.SkinnedModel model)
    {
        var cullMode = UseAutomaticModelCulling
            ? (model.PreferCullClockwiseFace ? CullMode.CullClockwiseFace : CullMode.CullCounterClockwiseFace)
            : ManualModelCullMode;

        return cullMode switch
        {
            CullMode.CullClockwiseFace => RasterizerState.CullClockwise,
            CullMode.CullCounterClockwiseFace => RasterizerState.CullCounterClockwise,
            _ => RasterizerState.CullNone
        };
    }
}
