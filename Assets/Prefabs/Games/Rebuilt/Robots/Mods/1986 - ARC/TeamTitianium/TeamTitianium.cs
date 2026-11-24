using System;
using System.Collections.Generic;
using Games.Rebuilt.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
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
/// Shot calculator (Use Shot Calculator on): while shooting, works out the launch angle and ball speed
///       with projectile physics from the distance AND height to the target, then sets the hood angle and
///       flywheel rpm to match. Tune Launch Efficiency until shots land.
/// Hood table (Use Shot Calculator off): hood angle looked up from the Hood Table by distance, flywheel
///       at Shooter Rpm. Either way the hood stows when not shooting.
///
/// Shoot on move: while driving, the turret, hood and flywheel aim at a point shifted against the robot's
///       velocity by the ball's flight time, so the ball's carried-over speed brings it into the target.
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

    /// <summary>One entry in the calculator trim table: small corrections to the calculator at this distance.</summary>
    [Serializable]
    public class CalcTrim
    {
        [Tooltip("Distance from the turret to the target, in feet.")]
        public float distanceFeet;
        [Tooltip("Degrees added to the calculator's hood angle at this distance (+ or -).")]
        public float hoodAngleOffset;
        [Tooltip("Percent added to the calculator's flywheel rpm at this distance (5 = 5% faster, -5 = 5% slower).")]
        public float rpmPercent;
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
    [Tooltip("While driving (faster than 1 ft/s), shoot without waiting for the turret at all. " +
             "Wait For Turret then only applies when the robot is standing still.")]
    [SerializeField] private bool ignoreTurretWhileMoving = true;

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

    [Header("Shot calculator")]
    [Tooltip("On: hood angle AND flywheel rpm are worked out with physics from distance and height. " +
             "Off: the hood uses the Hood Table and the flywheel uses Shooter Rpm.")]
    [SerializeField] private bool useShotCalculator = true;
    [Tooltip("Optional: an empty object placed where fuel leaves the shooter. If empty, the hood joint " +
             "(or the turret) is used.")]
    [SerializeField] private Transform shotOrigin;
    [Tooltip("Aim this many inches above (+) or below (-) the target point.")]
    [SerializeField] private float aimHeightOffsetInches = 0f;
    [Tooltip("Launch angle (degrees above flat) when the hood angle is 0.")]
    [SerializeField] private float launchAngleAtHoodZero = 75f;
    [Tooltip("Tick if raising the hood makes the shot FLATTER (most hoods). Untick if it makes it steeper.")]
    [SerializeField] private bool hoodFlattensShot = true;
    [Tooltip("Degrees steeper than the lowest-speed arc. Higher = loftier shot that drops into the goal.")]
    [SerializeField] private float arcBiasDegrees = 8f;
    [Tooltip("Flywheel wheel radius, inches.")]
    [SerializeField, Min(0.1f)] private float flywheelRadiusInches = 2f;
    [Tooltip("Ball speed ÷ wheel surface speed. Tune this: shots short → lower it, shots long → raise it.")]
    [SerializeField, Range(0.05f, 1.5f)] private float launchEfficiency = 0.5f;
    [Tooltip("The calculator never asks for less / more flywheel rpm than this.")]
    [SerializeField] private float minShooterRpm = 1000f;
    [SerializeField] private float maxShooterRpm = 6000f;
    [Tooltip("Only feed when the calculator says the shot is possible.")]
    [SerializeField] private bool onlyShootInRange = false;
    [Tooltip("Fine-tuning for the calculator: at each distance, nudge its hood angle (degrees) and flywheel " +
             "rpm (percent). Rows in any order; values between rows are blended; leave empty for no change.")]
    [SerializeField] private CalcTrim[] calculatorTrimTable =
    {
        new CalcTrim { distanceFeet = 6f,  hoodAngleOffset = 0f, rpmPercent = 0f },
        new CalcTrim { distanceFeet = 12f, hoodAngleOffset = 0f, rpmPercent = 0f },
        new CalcTrim { distanceFeet = 18f, hoodAngleOffset = 0f, rpmPercent = 0f },
    };

    [Header("Shoot on move")]
    [Tooltip("Lead the shot while driving: aim at where the target will be relative to the moving robot.")]
    [SerializeField] private bool shootOnMove = true;
    [Tooltip("How much of the robot's speed to correct for (1 = all of it). Lower if shots overshoot " +
             "the way you're driving, raise if they lag behind.")]
    [SerializeField, Range(0f, 2f)] private float moveCompensation = 1f;
    [Tooltip("Extra seconds added to the ball's flight time (covers feed delay). Raise if moving shots " +
             "still trail behind the way you're driving.")]
    [SerializeField, Min(0f)] private float extraLeadTime = 0.1f;
    [Tooltip("How quickly the robot-speed reading follows the real speed (1 = instantly, lower = smoother).")]
    [SerializeField, Range(0.01f, 1f)] private float velocitySmoothing = 0.3f;

    [Header("Spindexer collider")]
    [Tooltip("Turn off the solid 'Wheel_InternalCore' collider that MoSim creates at runtime for every " +
             "spindexer roller. The rollers still spin and still push fuel.")]
    [SerializeField] private bool disableSpindexerCores = true;

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

    [Header("Bumpers")]
    [Tooltip("Red bumper set (object holding the red bumpers). Shown on the red alliance, hidden on blue.")]
    [SerializeField] private GameObject redBumpers;
    [Tooltip("Blue bumper set. Shown on the blue alliance, hidden on red.")]
    [SerializeField] private GameObject blueBumpers;
    [Tooltip("Optional instead of / as well as the sets above: bumper fabric renderers to recolor.")]
    [SerializeField] private Renderer[] bumperRenderers;
    [SerializeField] private Material redBumperMaterial;
    [SerializeField] private Material blueBumperMaterial;

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
    private bool _spindexerShooting;
    private float _spindexerClock;
    private float _shootingClock;
    private bool _intakeAgitating;
    private bool _spindexerKicking;
    private float _distanceFeet = -1f;
    private float _hoodTarget;
    private float _shooterTargetRpm;
    private float _heightDiffFeet;
    private float _launchAngle;
    private float _exitSpeedFps;
    private bool _shotInRange = true;
    private float _trimHood;
    private float _trimRpmPercent;
    private Vector3 _realTarget;
    private bool _hasRealTarget;
    private Vector3 _aimPoint;
    private Vector3 _moveVelocity;
    private float _timeOfFlight;
    private float _leadFeet;
    private string _shotNote = "";
    private readonly Dictionary<InteractionRoller, float> _rollerCommands = new Dictionary<InteractionRoller, float>();
    private bool _coresDisabled;
    private Alliance _appliedBumperAlliance;
    private bool _bumpersApplied;

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

        ApplyBumpers();

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

    // StateHandler calls one of these every frame, before UpdateMechanisms(). They point the turret at the
    // real target and remember it; UpdateMechanisms() then moves the aim point for shoot-on-move.
    protected override void AllianceZone()
    {
        if (!turret) return;
        turret.ClearTarget();      // drop last frame's shoot-on-move aim point first
        turret.SetDefaultMap(GenericTurret.DefaultMap.Hub);
        RememberRealTarget();
    }

    protected override void NeutralZone()
    {
        if (!turret) return;
        turret.ClearTarget();      // drop last frame's shoot-on-move aim point first
        turret.SetPassMapClosest();
        RememberRealTarget();
    }

    private void RememberRealTarget()
    {
        _realTarget = turret.GetCurrentTarget();
        _hasRealTarget = _realTarget != Vector3.zero;
    }

    /// <summary>True once the shooter has spun up (and the turret is aimed, if Wait For Turret is on).</summary>
    protected override bool Aligned()
    {
        bool spunUp = _spinUpTimer >= spinUpTime;
        bool turretReady = !WaitingForTurretNow() || (turret && Mathf.Abs(turret.GetError()) < turretTolerance);
        bool rangeOk = !onlyShootInRange || !useShotCalculator || _shotInRange;
        return spunUp && turretReady && rangeOk;
    }

    /// <summary>True when feeding should wait for the turret right now (never while driving, if Ignore Turret While Moving is on).</summary>
    private bool WaitingForTurretNow()
    {
        if (!waitForTurret) return false;
        bool moving = _moveVelocity.magnitude * 3.28084f > 1f;
        return !(moving && ignoreTurretWhileMoving);
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
        if (disableSpindexerCores) DisableSpindexerCores();
        if (!_bumpersApplied || Alliance != _appliedBumperAlliance) ApplyBumpers();

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

        // With a kicker, the kicker is what actually fires fuel, so the spindexer only needs the shooter
        // spun up: it keeps fuel moving toward the kicker even while the turret is still lining up or the
        // robot is driving. With no kicker, the spindexer IS the feeder, so it waits for the full check.
        bool hasKicker = Count(kickerRollers) > 0;
        _spindexerShooting = hasKicker ? (_shootRequested && _spinUpTimer >= spinUpTime) : _feeding;

        // Spindexer clock runs whenever the spindexer is being driven, so kicks keep their rhythm.
        bool spindexerActive = _spindexerShooting || (spindexWhileIntaking && intaking);
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

        // Shoot on move: work out where to aim, then point the turret there.
        UpdateAimPoint();

        // Hood + flywheel: aim from the distance to the aim point.
        _distanceFeet = DistanceToTargetFeet();
        bool aiming = (_shootRequested || hoodAlwaysTrack) && _distanceFeet >= 0f;

        if (!aiming || !useShotCalculator)
        {
            _trimHood = 0f;
            _trimRpmPercent = 0f;
        }

        if (!aiming)
        {
            _hoodTarget = hoodStowAngle;
            _shooterTargetRpm = shooterRpm;
            _shotInRange = true;
            _shotNote = _distanceFeet < 0f ? "no target" : "";
        }
        else if (useShotCalculator)
        {
            CalculateShot();
        }
        else
        {
            _hoodTarget = HoodAngleForDistance(_distanceFeet);
            _shooterTargetRpm = shooterRpm;
            _shotInRange = true;
            _shotNote = "table";
        }
    }

    /// <summary>
    /// Each Interaction Roller builds a solid "Wheel_InternalCore" collider when it starts. This finds the
    /// ones on the spindexer rollers and keeps them switched off. It checks every frame, so a core that is
    /// created late (or re-enabled when the robot respawns) is caught too.
    /// </summary>
    private void DisableSpindexerCores()
    {
        if (spindexerRollers == null) { _coresDisabled = true; return; }

        bool allFound = true;
        foreach (var r in spindexerRollers)
        {
            if (r == null || !r.roller) continue;

            Transform core = r.roller.transform.Find("Wheel_InternalCore");
            if (core == null) { allFound = false; continue; }

            foreach (var col in core.GetComponents<Collider>()) if (col.enabled) col.enabled = false;
        }
        _coresDisabled = allFound;
    }

    /// <summary>Shows the bumper set and/or bumper color that matches this robot's alliance.</summary>
    private void ApplyBumpers()
    {
        Alliance alliance = Alliance;
        bool isRed = alliance == Alliance.Red;
        bool isBlue = alliance == Alliance.Blue;
        if (!isRed && !isBlue) return;      // alliance not decided yet (e.g. spawn select): try again next frame

        if (redBumpers) redBumpers.SetActive(isRed);
        if (blueBumpers) blueBumpers.SetActive(isBlue);

        Material mat = isRed ? redBumperMaterial : blueBumperMaterial;
        if (mat && bumperRenderers != null)
        {
            foreach (var r in bumperRenderers)
            {
                if (!r) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }
        }

        _appliedBumperAlliance = alliance;
        _bumpersApplied = true;
    }

    // ---------------------------------------------------------------------------------------- shot calculator

    /// <summary>
    /// Projectile math (no air drag): picks a launch angle the hood can reach, works out the ball speed
    /// that lands it on the target, then converts that speed to flywheel rpm and the angle to a hood angle.
    /// </summary>
    private void CalculateShot()
    {
        SolveShot(_aimPoint, out float angle, out float speed, out _, out float h);
        _heightDiffFeet = h * 3.28084f;
        _shotNote = "";

        if (float.IsNaN(speed))
        {
            _shotInRange = false;
            _shotNote = "target too high for hood range";
            speed = MaxBallSpeed();
        }
        else
        {
            _shotInRange = true;
        }

        float rpm = BallSpeedToRpm(speed);
        if (rpm > maxShooterRpm)
        {
            _shotInRange = false;
            _shotNote = "too far: needs " + rpm.ToString("0") + " rpm";
        }

        // Fine-tuning: per-distance corrections from the Calculator Trim Table.
        GetTrim(_distanceFeet, out _trimHood, out _trimRpmPercent);
        rpm *= 1f + _trimRpmPercent / 100f;

        _shooterTargetRpm = Mathf.Clamp(rpm, minShooterRpm, maxShooterRpm);
        _hoodTarget = Mathf.Clamp(HoodForLaunchAngle(angle) + _trimHood, hoodMinAngle, hoodMaxAngle);

        // Show the FINAL shot (after trim), so table edits are visible in the debug box.
        _launchAngle = LaunchAngleForHood(_hoodTarget);
        _exitSpeedFps = _shooterTargetRpm / 60f * 2f * Mathf.PI * flywheelRadiusInches * 0.0254f * launchEfficiency * 3.28084f;
    }

    /// <summary>
    /// Projectile solve for one target point: picks a launch angle the hood can reach and the ball speed
    /// (m/s, relative to the robot) that lands on it. speed is NaN if the hood can't make the shot.
    /// </summary>
    private void SolveShot(Vector3 target, out float angle, out float speed, out float d, out float h)
    {
        Vector3 origin = ShotOriginPosition();
        Vector3 flat = target - origin;
        flat.y = 0f;
        d = flat.magnitude;                                                      // meters, flat
        h = (target.y - origin.y) + aimHeightOffsetInches * 0.0254f;              // meters, up is +
        float g = Physics.gravity.magnitude;

        // Launch angles the hood can physically make.
        float angleAtMin = LaunchAngleForHood(hoodMinAngle);
        float angleAtMax = LaunchAngleForHood(hoodMaxAngle);
        float lowestAngle = Mathf.Min(angleAtMin, angleAtMax);
        float highestAngle = Mathf.Max(angleAtMin, angleAtMax);

        // The angle that needs the least speed is halfway between straight up and the line to the target.
        float minSpeedAngle = 45f + 0.5f * Mathf.Atan2(h, d) * Mathf.Rad2Deg;
        angle = Mathf.Clamp(minSpeedAngle + arcBiasDegrees, lowestAngle, highestAngle);
        speed = SpeedForAngle(angle, d, h, g);

        // Too flat to clear the height: try the steepest angle the hood allows instead.
        if (float.IsNaN(speed))
        {
            angle = highestAngle;
            speed = SpeedForAngle(angle, d, h, g);
        }
    }

    /// <summary>
    /// Shoot on move. The ball keeps the robot's sideways speed after it leaves, so aim at a point shifted
    /// back by (robot velocity × flight time). Flight time depends on the aim point, so it is refined a
    /// few times. Then the turret is pointed at that aim point.
    /// </summary>
    private void UpdateAimPoint()
    {
        if (!turret || !_hasRealTarget)
        {
            _aimPoint = _realTarget;
            _timeOfFlight = 0f;
            _leadFeet = 0f;
            return;
        }

        Vector3 velocity = rb ? rb.velocity : Vector3.zero;
        velocity.y = 0f;
        _moveVelocity = Vector3.Lerp(_moveVelocity, velocity, velocitySmoothing);

        Vector3 aim = _realTarget;
        if (shootOnMove)
        {
            for (int i = 0; i < 4; i++)
            {
                SolveShot(aim, out float angle, out float speed, out float d, out _);
                float horizontalSpeed = speed * Mathf.Cos(angle * Mathf.Deg2Rad);
                if (float.IsNaN(horizontalSpeed) || horizontalSpeed < 0.1f) break;

                _timeOfFlight = d / horizontalSpeed;
                aim = _realTarget - _moveVelocity * ((_timeOfFlight + extraLeadTime) * moveCompensation);
            }

            turret.SetTarget(aim);
        }
        else
        {
            _timeOfFlight = 0f;
        }

        _aimPoint = aim;
        Vector3 lead = aim - _realTarget;
        lead.y = 0f;
        _leadFeet = lead.magnitude * 3.28084f;
    }

    /// <summary>Blends the two nearest rows of the Calculator Trim Table for this distance. Empty table = no trim.</summary>
    private void GetTrim(float feet, out float hoodOffset, out float rpmPercent)
    {
        hoodOffset = 0f;
        rpmPercent = 0f;
        if (calculatorTrimTable == null || calculatorTrimTable.Length == 0 || feet < 0f) return;

        var rows = new List<CalcTrim>();
        foreach (var row in calculatorTrimTable) if (row != null) rows.Add(row);
        if (rows.Count == 0) return;
        rows.Sort((a, b) => a.distanceFeet.CompareTo(b.distanceFeet));

        if (feet <= rows[0].distanceFeet)
        {
            hoodOffset = rows[0].hoodAngleOffset;
            rpmPercent = rows[0].rpmPercent;
            return;
        }
        var last = rows[rows.Count - 1];
        if (feet >= last.distanceFeet)
        {
            hoodOffset = last.hoodAngleOffset;
            rpmPercent = last.rpmPercent;
            return;
        }
        for (int i = 0; i < rows.Count - 1; i++)
        {
            var a = rows[i];
            var b = rows[i + 1];
            if (feet < a.distanceFeet || feet > b.distanceFeet) continue;
            float span = b.distanceFeet - a.distanceFeet;
            float t = span > 0.0001f ? (feet - a.distanceFeet) / span : 0f;
            hoodOffset = Mathf.Lerp(a.hoodAngleOffset, b.hoodAngleOffset, t);
            rpmPercent = Mathf.Lerp(a.rpmPercent, b.rpmPercent, t);
            return;
        }
    }

    /// <summary>Ball speed (m/s) to land at flat distance d and height h when launched at this angle. NaN if impossible.</summary>
    private static float SpeedForAngle(float angleDeg, float d, float h, float g)
    {
        float a = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(a);
        float drop = d * Mathf.Tan(a) - h;            // must be positive: the arc has to climb above the target line
        if (cos <= 0.0001f || drop <= 0.0001f) return float.NaN;
        return Mathf.Sqrt(g * d * d / (2f * cos * cos * drop));
    }

    private float LaunchAngleForHood(float hood) =>
        hoodFlattensShot ? launchAngleAtHoodZero - hood : launchAngleAtHoodZero + hood;

    private float HoodForLaunchAngle(float launch) =>
        hoodFlattensShot ? launchAngleAtHoodZero - launch : launch - launchAngleAtHoodZero;

    private float BallSpeedToRpm(float metersPerSecond)
    {
        float wheelCircumference = 2f * Mathf.PI * flywheelRadiusInches * 0.0254f;
        return metersPerSecond / (wheelCircumference * launchEfficiency) * 60f;
    }

    private float MaxBallSpeed()
    {
        float wheelCircumference = 2f * Mathf.PI * flywheelRadiusInches * 0.0254f;
        return maxShooterRpm / 60f * wheelCircumference * launchEfficiency;
    }

    private Vector3 ShotOriginPosition()
    {
        if (shotOrigin) return shotOrigin.position;
        if (hoodJoint) return hoodJoint.transform.position;
        Transform from = turret.GetReferencePoint();
        return from ? from.position : turret.transform.position;
    }

    // ---------------------------------------------------------------------------------------- hood

    /// <summary>Flat (ignoring height) distance from the turret to its current target, in feet. -1 if no target.</summary>
    private float DistanceToTargetFeet()
    {
        if (!turret || !_hasRealTarget) return -1f;  // the turret has no target yet

        Vector3 origin = ShotOriginPosition();
        Vector3 flat = _aimPoint - origin;
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
        if (_spindexerShooting) spindexerSpeed = spindexerRpm;
        else if (spindexWhileIntaking && intaking) spindexerSpeed = spindexerIntakingRpm;
        if (_spindexerKicking) spindexerSpeed = -spindexerSpeed * spindexerReverseFraction;

        // 3. Shooter and kicker. The kicker pauses during spindexer kicks so it doesn't fire half a ball.
        Queue(shooterRollers, _shootRequested ? _shooterTargetRpm : 0f);
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
            $"Robot state: {robotState}   Alliance: {Alliance}\n" +
            $"Right trigger (shoot): {(OuttakeAction ? "HELD" : "not held")}   " +
            $"Left trigger: {(IntakeAction ? "HELD" : "not held")}\n" +
            $"Intake toggle (left bumper): {(_intakeToggled ? "ON" : "off")}   Mode: {_intakeMode}   " +
            $"Slide target: {_targetInches:0.0} in\n" +
            $"Spin-up: {_spinUpTimer:0.00} / {spinUpTime:0.00} s   Feeding: {(_feeding ? "YES" : "no")}   " +
            $"Wait For Turret: {(waitForTurret ? "ON" : "off")}   Only In Range: {(onlyShootInRange ? "ON" : "off")}\n" +
            $"Feed waiting for: {FeedBlockReason()}   Spindexer running: {(_spindexerShooting ? "YES" : "no")}\n" +
            $"Agitating  spindexer kick: {(_spindexerKicking ? "YES" : "no")}   intake pump: {(_intakeAgitating ? "YES" : "no")}\n" +
            $"Distance to target: {(_distanceFeet >= 0f ? _distanceFeet.ToString("0.0") + " ft" : "no target")}   " +
            $"Hood target: {_hoodTarget:0.0}°   Hood actual: {HoodActual()}\n" +
            $"Shot: {(useShotCalculator ? "calculator" : "table")}   height {_heightDiffFeet:0.0} ft   " +
            $"launch {_launchAngle:0.0}°   ball {_exitSpeedFps:0.0} ft/s   flywheel {_shooterTargetRpm:0} rpm   " +
            $"{(_shotInRange ? "IN RANGE" : "OUT OF RANGE")} {_shotNote}\n" +
            $"Trim at this distance: hood {_trimHood:+0.0;-0.0;0}°   rpm {_trimRpmPercent:+0.0;-0.0;0}%\n" +
            $"Shoot on move: {(shootOnMove ? "ON" : "off")}   robot {_moveVelocity.magnitude * 3.28084f:0.0} ft/s   " +
            $"flight {_timeOfFlight:0.00} s   lead {_leadFeet:0.0} ft\n" +
            $"Rollers  intake: {Count(intakeRollerList)}   spindexer: {Count(spindexerRollers)}   " +
            $"kicker: {Count(kickerRollers)}   shooter: {Count(shooterRollers)}\n" +
            $"Spindexer rpm: {RealRpm(spindexerRollers)}   Kicker rpm: {RealRpm(kickerRollers)}";

        GUI.Box(new Rect(10, 10, 680, 275), GUIContent.none);
        GUI.Label(new Rect(18, 14, 670, 270), text);
    }

    /// <summary>What is stopping Feed() right now, in plain words (for the debug box).</summary>
    private string FeedBlockReason()
    {
        if (!_shootRequested) return "right trigger";
        var reasons = new List<string>();
        if (_spinUpTimer < spinUpTime) reasons.Add("spin-up");
        if (WaitingForTurretNow())
        {
            if (!turret) reasons.Add("turret (none assigned)");
            else
            {
                float error = Mathf.Abs(turret.GetError());
                if (error >= turretTolerance) reasons.Add($"turret aim ({error:0.0}° off, needs < {turretTolerance:0.0}°)");
            }
        }
        if (onlyShootInRange && useShotCalculator && !_shotInRange) reasons.Add("shot out of range");
        return reasons.Count == 0 ? "nothing (feeding)" : string.Join(", ", reasons);
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
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (!n.Contains("bumper")) continue;
            if (!redBumpers && n.Contains("red")) redBumpers = t.gameObject;
            else if (!blueBumpers && n.Contains("blue")) blueBumpers = t.gameObject;
        }
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
                  $"red bumpers {Found(redBumpers)}, blue bumpers {Found(blueBumpers)}, " +
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