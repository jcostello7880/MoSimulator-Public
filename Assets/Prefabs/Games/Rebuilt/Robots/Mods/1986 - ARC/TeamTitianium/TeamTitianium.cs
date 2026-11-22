using System;
using System.Collections.Generic;
using Games.Rebuilt.Robots;
using MoSimCore.BaseClasses.GameManagement;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Team Titianium: drivetrain + rack-and-pinion slide intake + spindexer + turret shooter.
/// Controls:
///   Left Bumper (Q)                       toggles the intake on/off (slide out + rollers / slide in).
///   Left Trigger (Left Shift), held       intakes while held; the slide stays out after.
///   Stow (D-pad down / Z) or Robot Special 2 (X / I)   stops the intake and brings the slide in.
///   Robot Special (Y / V)                 outtakes through the intake.
///   Right Trigger (K), held               spins up the shooter, then feeds with the spindexer and kicker.
///
/// Hood: while shooting, the hood angle is looked up from the Hood Table using the flat distance from
///       the turret to its target (hub or pass target), blending between rows. Otherwise it stows.
///
/// Auto agitation:
///   Spindexer: while feeding (and while intaking, if enabled) it runs forward and gives short reverse
///              "kicks" on a timer, so fuel doesn't jam.
///   Intake:    while shooting with the slide out and not intaking, the slide pumps in and out and the
///              intake rollers push inward, to shove the last fuel toward the spindexer.
///
/// Every roller is listed on its own with a Reverse box, so rollers that must spin opposite ways are
/// handled per roller. The arrow drawn on each Interaction Roller in the Scene view shows which way a
/// positive rpm turns it: tick Reverse on any that point the wrong way.
///
/// Why all roller commands go through ApplyRollers(): an Interaction Roller accepts only ONE speed
/// command per physics frame (later ones are ignored, and Stop() counts as one). So this script decides
/// every roller's speed first, then sends each roller exactly one command, once, at the end of the frame.
/// </summary>
public class TeamTitianium : RebuiltTurretRobot
{
    /// <summary>One roller and the direction it should spin.</summary>
    [Serializable]
    public class RollerSpin
    {
        [Tooltip("The Interaction Roller to spin.")]
        public InteractionRoller roller;
        [Tooltip("Tick to spin this roller the opposite way from the others in its list.")]
        public bool reverse;
        [Tooltip("Speed relative to the list's rpm (1 = same speed, 0.5 = half). 0 = never spins.")]
        public float multiplier = 1f;
    }

    /// <summary>One entry in the hood table: at this distance, use this hood angle.</summary>
    [Serializable]
    public class HoodPoint
    {
        [Tooltip("Distance from the turret to the target, in feet.")]
        public float distanceFeet;
        [Tooltip("Hood angle to use at that distance, in degrees (0 = hood fully down).")]
        public float hoodAngle;
    }

    [Header("Intake slide")]
    [Tooltip("The slide's GenericJoint (child with Rigidbody + ConfigurableJoint + GenericJoint).")]
    [SerializeField] private GenericJoint intakeSlide;
    [Tooltip("The joint axis the slide travels along.")]
    [SerializeField] private JointAxis intakeAxis = JointAxis.Z;
    [Tooltip("Slide travel from stowed to deployed, inches.")]
    [SerializeField] private float travelInches = 12f;
    [SerializeField] private PidConstants intakePid = new PidConstants(4, 0, 0.0005f, 0.6f, 0);

    [Header("Joints")]
    [SerializeField] private GenericTurret turret;

    [Header("PID")]
    [SerializeField] private PidConstants turretPID;

    [Header("Intake rollers")]
    [Tooltip("Optional: an existing RollerGroup for the intake. Rollers that are ALSO in a list below " +
             "are controlled by the list, not by this group.")]
    [SerializeField] private RollerGroup intakeRollers;
    [Tooltip("Intake rollers listed one by one, each with its own Reverse box.")]
    [SerializeField] private RollerSpin[] intakeRollerList;
    [Tooltip("Roller rpm while intaking (negative = spin the other way).")]
    [SerializeField] private float intakeRpm = 2000f;
    [Tooltip("Roller rpm while outtaking (Robot Special).")]
    [SerializeField] private float outtakeRpm = -2000f;

    [Header("Shooter rollers")]
    [Tooltip("Spindexer roller(s). Each has its own Reverse box.")]
    [SerializeField] private RollerSpin[] spindexerRollers;
    [Tooltip("Kicker / feeder / uptake roller(s) that push fuel up into the shooter (optional).")]
    [SerializeField] private RollerSpin[] kickerRollers;
    [Tooltip("Shooter flywheel roller(s). Tick Reverse on the one(s) that must spin opposite.")]
    [SerializeField] private RollerSpin[] shooterRollers;

    [Header("Shooter values")]
    [SerializeField] private float spindexerRpm = 500f;
    [SerializeField] private float kickerRpm = 3000f;
    [SerializeField] private float shooterRpm = 3000f;
    [Tooltip("Seconds the shooter spins up after you press the trigger before fuel is fed in.")]
    [SerializeField, Min(0f)] private float spinUpTime = 0.5f;
    [Tooltip("Only feed once the turret is aimed within the tolerance below. Leave off while testing.")]
    [SerializeField] private bool waitForTurret = false;
    [Tooltip("How close (degrees) the turret must be to its target when Wait For Turret is on.")]
    [SerializeField, Min(0f)] private float turretTolerance = 5f;

    [Header("Hood")]
    [Tooltip("The hood's GenericJoint (child with Rigidbody + ConfigurableJoint + GenericJoint). " +
             "Its angle must read 0 with the hood fully down.")]
    [SerializeField] private GenericJoint hoodJoint;
    [Tooltip("The joint axis the hood pivots around.")]
    [SerializeField] private JointAxis hoodAxis = JointAxis.X;
    [SerializeField] private PidConstants hoodPid;
    [Tooltip("Distance → hood angle. Add as many rows as you like, in any order; angles between rows " +
             "are blended. These starting numbers are guesses: tune them by testing shots.")]
    [SerializeField] private HoodPoint[] hoodTable =
    {
        new HoodPoint { distanceFeet = 4f,  hoodAngle = 5f  },
        new HoodPoint { distanceFeet = 8f,  hoodAngle = 14f },
        new HoodPoint { distanceFeet = 12f, hoodAngle = 21f },
        new HoodPoint { distanceFeet = 16f, hoodAngle = 27f },
        new HoodPoint { distanceFeet = 20f, hoodAngle = 32f },
    };
    [Tooltip("The hood never goes below / above these angles, in degrees.")]
    [SerializeField] private float hoodMinAngle = 0f;
    [SerializeField] private float hoodMaxAngle = 40f;
    [Tooltip("Angle the hood sits at when not shooting (0 = fully down, safest for driving under things).")]
    [SerializeField] private float hoodStowAngle = 0f;
    [Tooltip("Keep aiming the hood even when not shooting, so it is already set when you pull the trigger.")]
    [SerializeField] private bool hoodAlwaysTrack = false;

    [Header("Spindexer agitation")]
    [Tooltip("Give the spindexer short reverse kicks on a timer so fuel doesn't jam.")]
    [SerializeField] private bool agitateSpindexer = true;
    [Tooltip("Seconds of forward spin between reverse kicks.")]
    [SerializeField, Min(0.05f)] private float spindexerForwardTime = 1.0f;
    [Tooltip("Seconds each reverse kick lasts.")]
    [SerializeField, Min(0f)] private float spindexerReverseTime = 0.15f;
    [Tooltip("Reverse kick speed as a fraction of the forward speed (0.5 = half speed backwards).")]
    [SerializeField, Range(0f, 2f)] private float spindexerReverseFraction = 0.6f;
    [Tooltip("Also spin the spindexer slowly (with kicks) while intaking, to spread fuel out.")]
    [SerializeField] private bool spindexWhileIntaking = true;
    [Tooltip("Spindexer rpm while intaking (only if Spindex While Intaking is on).")]
    [SerializeField] private float spindexerIntakingRpm = 200f;

    [Header("Intake agitation")]
    [Tooltip("While shooting with the slide out and not intaking, pump the slide in and out.")]
    [SerializeField] private bool agitateIntake = true;
    [Tooltip("Seconds of shooting before the intake starts pumping.")]
    [SerializeField, Min(0f)] private float intakeAgitationDelay = 1.0f;
    [Tooltip("How far the slide pulls in on each pump, inches from fully out.")]
    [SerializeField, Min(0f)] private float intakeAgitationInches = 5f;
    [Tooltip("Seconds for one full pump (in and back out).")]
    [SerializeField, Min(0.1f)] private float intakeAgitationPeriod = 0.8f;
    [Tooltip("Intake roller rpm while pumping, to push fuel inward (0 = rollers stopped).")]
    [SerializeField] private float intakeAgitationRollerRpm = 1000f;

    [Header("Scoop / ramp (optional)")]
    [SerializeField] private Collider[] rampColliders;
    [SerializeField, Range(0f, 1f)] private float rampMinFraction = 0.8f;

    [Header("Drive")]
    [SerializeField, Range(0f, 1f)] private float driveMultiplier = 1f;
    [SerializeField, Range(0f, 1f)] private float intakingDriveMultiplier = 1f;

    [Header("Debug")]
    [Tooltip("Shows a live readout in the top-left of the Game view while playing.")]
    [SerializeField] private bool showDebug = true;

    private enum IntakeMode { Stopped, Intaking, Outtaking }

    private bool _deployed;
    private bool _intakeToggled;
    private bool _rampEnabled = true;
    private float _targetInches;
    private Vector3 _slideStart;

    private IntakeMode _intakeMode = IntakeMode.Stopped;
    private bool _shootRequested;
    private float _spinUpTimer;
    private bool _feeding;
    private float _spindexerClock;
    private float _shootingClock;
    private bool _intakeAgitating;
    private bool _spindexerKicking;
    private float _distanceFeet = -1f;
    private float _hoodTarget;
    private readonly Dictionary<InteractionRoller, float> _rollerCommands = new Dictionary<InteractionRoller, float>();

    /// <summary>How far the slide is actually out, 0 (stowed) to 1 (deployed).</summary>
    public float ExtendedFraction =>
        intakeSlide ? Vector3.Distance(intakeSlide.transform.localPosition, _slideStart) / 0.0254f / travelInches : 0f;

    public bool IsDeployed => _deployed;

    protected override void Init()
    {
        // Report missing references plainly instead of crashing Init.
        var missing = new List<string>();
        if (!intakeSlide) missing.Add(nameof(intakeSlide));
        if (!turret) missing.Add(nameof(turret));
        if (!intakeRollers && Count(intakeRollerList) == 0) missing.Add("intake rollers");
        if (Count(spindexerRollers) == 0) missing.Add(nameof(spindexerRollers));
        if (Count(shooterRollers) == 0) missing.Add(nameof(shooterRollers));
        if (missing.Count > 0)
        {
            Debug.LogError($"{name}: unassigned {string.Join(", ", missing)} on TeamTitianium. " +
                           "Right-click the component > Auto-find references, or drag them in.", this);
        }
        WarnAboutZeroMultipliers();
        if (!hoodJoint)
            Debug.LogWarning($"{name}: no Hood Joint assigned, so the hood won't move. Drag it in or use Auto-find references.", this);
        if (hoodTable == null || hoodTable.Length == 0)
            Debug.LogWarning($"{name}: the Hood Table is empty, so the hood will stay at its stow angle.", this);

        if (hoodJoint) hoodJoint.SetPid(hoodPid);

        if (intakeSlide)
        {
            _slideStart = intakeSlide.transform.localPosition;
            intakeSlide.SetPid(intakePid);
        }

        if (turret) turret.SetPid(turretPID);

        // Always call this, even if something above is missing, so the robot still finishes setting up.
        // It registers HandleSwm, StateHandler and ShooterHandler (which calls Shooting / Intaking / Idling).
        base.Init();

        int field = LayerMask.GetMask("Field");
        if (rampColliders != null)
        {
            foreach (var c in rampColliders) if (c) c.excludeLayers |= field;
        }
        SetRamp(false);

        Scheduler.AddSubsystems(Intake, Drive);
        // Registered after base.Init(), so these run after ShooterHandler every physics frame, in this order.
        Scheduler.AddProtectedSystem(UpdateMechanisms, UpdateJoints, ApplyRollers);
        Scheduler.AddLateRecursive(UpdatePids);
    }

    // ---------------------------------------------------------------------------------------- turret

    protected override void AllianceZone()
    {
        if (turret) turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
    }

    protected override void NeutralZone()
    {
        if (turret) turret.SetPassMapClosest();
    }

    /// <summary>True once the shooter has spun up (and the turret is aimed, if Wait For Turret is on).</summary>
    protected override bool Aligned()
    {
        bool spunUp = _spinUpTimer >= spinUpTime;
        bool turretReady = !waitForTurret || (turret && Mathf.Abs(turret.GetError()) < turretTolerance);
        return spunUp && turretReady;
    }

    protected override void HandleSwm()
    {
        // Shoot-on-move: only runs once swmRef (with a parent) is assigned in the Inspector.
        if (rb != null && swmRef != null && swmRef.parent != null)
        {
            base.HandleSwm();
        }
    }

    // ---------------------------------------------------------------------------------------- shooter state
    // RebuiltTurretRobot.ShooterHandler calls exactly one of these every physics frame. They only record
    // whether the right trigger is held; UpdateMechanisms() and ApplyRollers() do the rest.

    protected override void Shooting() => _shootRequested = true;
    protected override void IntakingAndShooting() => _shootRequested = true;
    protected override void Intaking() => _shootRequested = false;
    protected override void Idling() => _shootRequested = false;

    // Feeding is handled in ApplyRollers(); a roller command here would be ignored (one per frame).
    protected override void Feed() { }

    // ---------------------------------------------------------------------------------------- intake input

    private void Intake()
    {
        // Left bumper: toggle the intake on/off. Turning it off also brings the slide in.
        if (ManualShoot.WasTriggered())
        {
            _intakeToggled = !_intakeToggled;
            if (!_intakeToggled) _deployed = false;
        }

        // Stow / Robot Special 2: everything off and in.
        if (StowAction || RobotSpecial2)
        {
            _intakeToggled = false;
            _deployed = false;
        }

        bool wantsIntake = IntakeAction || _intakeToggled;
        if (wantsIntake || RobotSpecial) _deployed = true;      // left trigger latches the slide out

        if (RobotSpecial) _intakeMode = IntakeMode.Outtaking;
        else if (wantsIntake) _intakeMode = IntakeMode.Intaking;
        else _intakeMode = IntakeMode.Stopped;
    }

    private void Drive()
    {
        bool intaking = _intakeMode == IntakeMode.Intaking;
        if (driveController) driveController.SetDriveMp(intaking ? intakingDriveMultiplier : driveMultiplier);
    }

    // ---------------------------------------------------------------------------------------- timing + agitation

    /// <summary>Updates the shooter / agitation timers and decides the slide target for this frame.</summary>
    private void UpdateMechanisms()
    {
        float dt = Time.deltaTime;
        bool intaking = _intakeMode == IntakeMode.Intaking;

        // Shooter spin-up and feed
        if (_shootRequested)
        {
            _spinUpTimer += dt;
            _shootingClock += dt;
        }
        else
        {
            _spinUpTimer = 0f;
            _shootingClock = 0f;
        }
        _feeding = _shootRequested && Aligned();

        // Spindexer clock runs whenever the spindexer is being driven, so kicks keep their rhythm.
        bool spindexerActive = _feeding || (spindexWhileIntaking && intaking);
        if (spindexerActive) _spindexerClock += dt;
        else _spindexerClock = 0f;

        float cycle = spindexerForwardTime + spindexerReverseTime;
        _spindexerKicking = agitateSpindexer && spindexerActive && spindexerReverseTime > 0f &&
                            (_spindexerClock % cycle) >= spindexerForwardTime;

        // Intake agitation: shooting, slide out, not intaking, after the delay.
        _intakeAgitating = agitateIntake && _shootRequested && _deployed && !intaking &&
                           _intakeMode != IntakeMode.Outtaking && _shootingClock >= intakeAgitationDelay;

        // Slide target
        if (!_deployed)
        {
            _targetInches = 0f;
        }
        else if (_intakeAgitating)
        {
            // Smooth in-and-out: 0 at full out, 1 at the most pulled-in point.
            float phase = ((_shootingClock - intakeAgitationDelay) % intakeAgitationPeriod) / intakeAgitationPeriod;
            float pull = 0.5f - 0.5f * Mathf.Cos(phase * 2f * Mathf.PI);
            _targetInches = travelInches - Mathf.Sign(travelInches) * intakeAgitationInches * pull;
        }
        else
        {
            _targetInches = travelInches;
        }

        SetRamp(ExtendedFraction >= rampMinFraction);

        // Hood: aim from the distance to whatever the turret is aiming at (hub, or pass target).
        _distanceFeet = DistanceToTargetFeet();
        bool aimHood = (_shootRequested || hoodAlwaysTrack) && _distanceFeet >= 0f;
        _hoodTarget = aimHood ? HoodAngleForDistance(_distanceFeet) : hoodStowAngle;
    }

    // ---------------------------------------------------------------------------------------- hood

    /// <summary>Flat (ignoring height) distance from the turret to its current target, in feet. -1 if no target.</summary>
    private float DistanceToTargetFeet()
    {
        if (!turret) return -1f;

        Vector3 target = turret.GetCurrentTarget();
        if (target == Vector3.zero) return -1f;     // the turret has no target yet

        Transform from = turret.GetReferencePoint();
        Vector3 origin = from ? from.position : turret.transform.position;

        Vector3 flat = target - origin;
        flat.y = 0f;
        return flat.magnitude * 3.28084f;           // meters → feet
    }

    /// <summary>Looks the distance up in the hood table, blending between the two nearest rows.</summary>
    private float HoodAngleForDistance(float feet)
    {
        if (hoodTable == null || hoodTable.Length == 0) return hoodStowAngle;

        // Copy and sort by distance, so table rows can be typed in any order.
        var rows = new List<HoodPoint>();
        foreach (var p in hoodTable) if (p != null) rows.Add(p);
        if (rows.Count == 0) return hoodStowAngle;
        rows.Sort((a, b) => a.distanceFeet.CompareTo(b.distanceFeet));

        float angle;
        if (feet <= rows[0].distanceFeet)
        {
            angle = rows[0].hoodAngle;                              // closer than the first row
        }
        else if (feet >= rows[rows.Count - 1].distanceFeet)
        {
            angle = rows[rows.Count - 1].hoodAngle;                 // farther than the last row
        }
        else
        {
            angle = rows[0].hoodAngle;
            for (int i = 0; i < rows.Count - 1; i++)
            {
                var a = rows[i];
                var b = rows[i + 1];
                if (feet >= a.distanceFeet && feet <= b.distanceFeet)
                {
                    float span = b.distanceFeet - a.distanceFeet;
                    float t = span > 0.0001f ? (feet - a.distanceFeet) / span : 0f;
                    angle = Mathf.Lerp(a.hoodAngle, b.hoodAngle, t);
                    break;
                }
            }
        }

        return Mathf.Clamp(angle, hoodMinAngle, hoodMaxAngle);
    }

    // ---------------------------------------------------------------------------------------- rollers

    /// <summary>Decides every roller's speed for this frame, then sends each roller exactly one command.</summary>
    private void ApplyRollers()
    {
        bool intaking = _intakeMode == IntakeMode.Intaking;
        _rollerCommands.Clear();

        // 1. Intake (lowest priority: a roller also listed under the shooter follows the shooter).
        float intakeSpeed;
        if (_intakeMode == IntakeMode.Outtaking) intakeSpeed = outtakeRpm;
        else if (intaking) intakeSpeed = intakeRpm;
        else if (_intakeAgitating) intakeSpeed = intakeAgitationRollerRpm;
        else intakeSpeed = 0f;
        Queue(intakeRollerList, intakeSpeed);

        // 2. Spindexer: forward, with reverse kicks when agitation is on.
        float spindexerSpeed = 0f;
        if (_feeding) spindexerSpeed = spindexerRpm;
        else if (spindexWhileIntaking && intaking) spindexerSpeed = spindexerIntakingRpm;
        if (_spindexerKicking) spindexerSpeed = -spindexerSpeed * spindexerReverseFraction;

        // 3. Shooter and kicker. The kicker pauses during spindexer kicks so it doesn't fire half a ball.
        Queue(shooterRollers, _shootRequested ? shooterRpm : 0f);
        Queue(spindexerRollers, spindexerSpeed);
        Queue(kickerRollers, _feeding && !_spindexerKicking ? kickerRpm : 0f);

        // 4. Send: exactly one command per roller. 0 rpm = Stop().
        foreach (var command in _rollerCommands)
        {
            if (!command.Key) continue;
            if (Mathf.Approximately(command.Value, 0f)) command.Key.Stop();
            else command.Key.OverrideRpm(command.Value);
        }

        // 5. Optional intake RollerGroup last: any roller already commanded above ignores this.
        if (intakeRollers)
        {
            if (Mathf.Approximately(intakeSpeed, 0f)) intakeRollers.Stop();
            else intakeRollers.OverrideRpm(intakeSpeed);
        }
    }

    private void Queue(RollerSpin[] list, float rpm)
    {
        if (list == null) return;
        foreach (var r in list)
        {
            if (r == null || !r.roller) continue;
            _rollerCommands[r.roller] = (r.reverse ? -rpm : rpm) * r.multiplier;
        }
    }

    // ---------------------------------------------------------------------------------------- joints

    // 0 = stowed: the prefab starts with the slide in.
    private void UpdateJoints()
    {
        if (intakeSlide)
            intakeSlide.SetLinearTarget(_targetInches).withAxis(intakeAxis).useAutomaticStartingOffset();

        if (turret) turret.Align();

        if (hoodJoint) hoodJoint.SetTargetAngle(_hoodTarget).withAxis(hoodAxis);
    }

    // Re-applied late so PID edits in the Inspector take effect during play.
    private void UpdatePids()
    {
        if (intakeSlide) intakeSlide.UpdatePid(intakePid);
        if (turret) turret.UpdatePid(turretPID);
        if (hoodJoint) hoodJoint.UpdatePid(hoodPid);
    }

    private void SetRamp(bool on)
    {
        if (on == _rampEnabled || rampColliders == null) return;
        _rampEnabled = on;
        foreach (var c in rampColliders) if (c) c.enabled = on;
    }

    // ---------------------------------------------------------------------------------------- debug readout

    private void OnGUI()
    {
        if (!showDebug || !Application.isPlaying) return;

        var gm = BaseGameManager.Instance;
        string robotState = gm == null ? "NO GAME MANAGER" : gm.RobotState.ToString();

        string text =
            $"TeamTitianium debug\n" +
            $"Robot state: {robotState}\n" +
            $"Right trigger (shoot): {(OuttakeAction ? "HELD" : "not held")}   " +
            $"Left trigger: {(IntakeAction ? "HELD" : "not held")}\n" +
            $"Intake toggle (left bumper): {(_intakeToggled ? "ON" : "off")}   Mode: {_intakeMode}   " +
            $"Slide target: {_targetInches:0.0} in\n" +
            $"Spin-up: {_spinUpTimer:0.00} / {spinUpTime:0.00} s   Feeding: {(_feeding ? "YES" : "no")}\n" +
            $"Agitating  spindexer kick: {(_spindexerKicking ? "YES" : "no")}   intake pump: {(_intakeAgitating ? "YES" : "no")}\n" +
            $"Distance to target: {(_distanceFeet >= 0f ? _distanceFeet.ToString("0.0") + " ft" : "no target")}   " +
            $"Hood target: {_hoodTarget:0.0}°   Hood actual: {HoodActual()}\n" +
            $"Rollers  intake: {Count(intakeRollerList)}   spindexer: {Count(spindexerRollers)}   " +
            $"kicker: {Count(kickerRollers)}   shooter: {Count(shooterRollers)}\n" +
            $"Spindexer rpm: {RealRpm(spindexerRollers)}   Kicker rpm: {RealRpm(kickerRollers)}";

        GUI.Box(new Rect(10, 10, 560, 190), GUIContent.none);
        GUI.Label(new Rect(18, 14, 550, 185), text);
    }

    private string HoodActual()
    {
        if (!hoodJoint) return "no hood";
        float angle = hoodJoint.GetAngle();
        return angle.ToString("0.0") + "°";
    }

    private static string RealRpm(RollerSpin[] list)
    {
        if (list == null) return "-";
        foreach (var r in list)
            if (r != null && r.roller) return r.roller.realRpm.ToString("0");
        return "-";
    }

    // ---------------------------------------------------------------------------------------- helpers

    private static int Count(RollerSpin[] list)
    {
        int n = 0;
        if (list != null) foreach (var r in list) if (r != null && r.roller) n++;
        return n;
    }

    private void WarnAboutZeroMultipliers()
    {
        void Check(RollerSpin[] list, string label)
        {
            if (list == null) return;
            foreach (var r in list)
                if (r != null && r.roller && Mathf.Approximately(r.multiplier, 0f))
                    Debug.LogWarning($"{name}: {label} roller '{r.roller.name}' has Multiplier 0, so it will never spin. Set it to 1.", this);
        }
        Check(intakeRollerList, "Intake");
        Check(spindexerRollers, "Spindexer");
        Check(kickerRollers, "Kicker");
        Check(shooterRollers, "Shooter");
    }

    // ---------------------------------------------------------------------------------------- editor setup

    private void Reset() => AutoFindReferences();

    /// <summary>
    /// Fills only empty fields. Rollers are sorted by the names of the roller object and its parents:
    /// "spindex" = spindexer, "kick"/"feed"/"uptake" = kicker, "shoot"/"flywheel" = shooter,
    /// anything else under the intake slide = intake. Reverse boxes start unticked: set them yourself.
    /// </summary>
    [ContextMenu("Auto-find references")]
    private void AutoFindReferences()
    {
        if (!intakeSlide)
        {
            GenericJoint any = null;
            foreach (var j in GetComponentsInChildren<GenericJoint>(true))
            {
                if (j.gameObject == gameObject) continue;
                if (any == null) any = j;
                if (j.name.ToLowerInvariant().Contains("intake")) { intakeSlide = j; break; }
            }
            if (!intakeSlide) intakeSlide = any;
        }
        if (!turret) turret = GetComponentInChildren<GenericTurret>(true);
        if (!hoodJoint)
        {
            foreach (var j in GetComponentsInChildren<GenericJoint>(true))
            {
                if (j == intakeSlide || j.gameObject == gameObject) continue;
                if (j.name.ToLowerInvariant().Contains("hood")) { hoodJoint = j; break; }
            }
        }

        var spindex = new List<RollerSpin>();
        var kick = new List<RollerSpin>();
        var shoot = new List<RollerSpin>();
        var intake = new List<RollerSpin>();

        foreach (var roller in GetComponentsInChildren<InteractionRoller>(true))
        {
            string path = PathName(roller.transform);
            var entry = new RollerSpin { roller = roller, multiplier = 1f };
            if (path.Contains("spindex")) spindex.Add(entry);
            else if (path.Contains("kick") || path.Contains("feed") || path.Contains("uptake")) kick.Add(entry);
            else if (path.Contains("shoot") || path.Contains("flywheel")) shoot.Add(entry);
            else if (intakeSlide && roller.transform.IsChildOf(intakeSlide.transform)) intake.Add(entry);
        }

        if (Count(spindexerRollers) == 0) spindexerRollers = spindex.ToArray();
        if (Count(kickerRollers) == 0) kickerRollers = kick.ToArray();
        if (Count(shooterRollers) == 0) shooterRollers = shoot.ToArray();
        if (Count(intakeRollerList) == 0 && !intakeRollers) intakeRollerList = intake.ToArray();

        if (intakeSlide && (rampColliders == null || rampColliders.Length == 0))
        {
            var ramps = new List<Collider>();
            foreach (var t in intakeSlide.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("ramp") || n.Contains("scoop")) ramps.AddRange(t.GetComponentsInChildren<Collider>(true));
            }
            rampColliders = ramps.ToArray();
        }

        Debug.Log($"{name}: slide {Found(intakeSlide)}, turret {Found(turret)}, hood {Found(hoodJoint)}, " +
                  $"intake rollers {Count(intakeRollerList)}{(intakeRollers ? " + group " + intakeRollers.name : "")}, " +
                  $"spindexer {Count(spindexerRollers)}, kicker {Count(kickerRollers)}, shooter {Count(shooterRollers)}, " +
                  $"{rampColliders?.Length ?? 0} ramp colliders. Now set the Reverse boxes.", this);
    }

    // Name of this object and every parent up to the robot, lower-case, so "Shooter/Roller (2)" still matches "shoot".
    private string PathName(Transform t)
    {
        var parts = new List<string>();
        for (; t != null && t != transform; t = t.parent) parts.Add(t.name.ToLowerInvariant());
        return string.Join("/", parts);
    }

    private static string Found(Object o) => o ? o.name : "NOT FOUND";
}