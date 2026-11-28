using System.Collections.Generic;
using Games.Rebuilt.Robots;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;

/// <summary>
/// The BroncoBots: drivetrain + slide-out intake + dye rotor (spinning hopper that feeds fuel) + turret shooter.
/// Controls:
///   Intake (Left Shift / Left Trigger)   deploys the slide and runs the intake rollers; the slide stays out.
///   Stow or Robot Special 2               brings the slide in.
///   Robot Special                         outtakes through the intake and runs the dye rotor backwards (unjam).
///   AutoShoot (Right Trigger), held       spins up the flywheel, then spins the dye rotor and kicker to shoot.
///
/// Every roller list comes in two halves: "Forward" and "Reversed". Put rollers that must spin the
/// opposite way (top vs bottom, left vs right) in the Reversed list. Plain arrays are used on purpose:
/// custom [Serializable] classes in a mod script arrive empty in the real MoSim game.
/// </summary>
public class TheBroncoBots : RebuiltTurretRobot
{
    [Header("Intake slide")]
    [Tooltip("The slide's GenericJoint (child with Rigidbody + ConfigurableJoint + GenericJoint).")]
    [SerializeField] private GenericJoint intakeSlide;
    [Tooltip("The joint axis the slide travels along.")]
    [SerializeField] private JointAxis intakeAxis = JointAxis.Z;
    [Tooltip("Slide travel from stowed to deployed, inches. Flip the sign if it slides the wrong way.")]
    [SerializeField] private float travelInches = 12f;
    [SerializeField] private PidConstants intakePid = new PidConstants(4, 0, 0.0005f, 0.6f, 0);

    [Header("Intake kicker bar")]
    [Tooltip("OPTION A, Unity Hinge Joint: the bar's HingeJoint (on a child of the slide with its own Rigidbody, " +
             "Connected Body = the slide's Rigidbody). If this is set, Kicker Bar below is ignored.")]
    [SerializeField] private HingeJoint kickerBarHinge;
    [Tooltip("Hinge spring strength. Higher = swings harder and holds its angle better.")]
    [SerializeField, Min(0f)] private float kickerBarSpring = 5000f;
    [Tooltip("Hinge spring damper. Raise it if the bar bounces at the end of its swing.")]
    [SerializeField, Min(0f)] private float kickerBarDamper = 200f;
    [Tooltip("OPTION B, no joint: an empty on the bar's hinge line, with the bar's Model and Physics as children. " +
             "It must be a child of the intake slide and must NOT have its own Rigidbody.")]
    [SerializeField] private Transform kickerBar;
    [Tooltip("Local axis the bar pivots around (1,0,0 = its red X arrow, 0,1,0 = green Y, 0,0,1 = blue Z).")]
    [SerializeField] private Vector3 kickerBarAxis = Vector3.right;
    [Tooltip("How far the bar swings out, degrees. Make it negative if it swings the wrong way. " +
             "With a Hinge Joint, keep it inside the hinge's limits if Use Limits is on.")]
    [SerializeField] private float kickerBarAngle = 90f;
    [Tooltip("How far out the slide has to be before the bar swings, 0 (in) to 1 (all the way out).")]
    [SerializeField, Range(0f, 1f)] private float kickerBarStartFraction = 0.8f;
    [Tooltip("How fast the bar swings, degrees per second (Option B only; the hinge spring sets its own speed).")]
    [SerializeField, Min(1f)] private float kickerBarSpeed = 360f;
    [Tooltip("When stowing, hold the slide out until the bar has folded back in, so it doesn't hit the frame.")]
    [SerializeField] private bool foldBarBeforeRetract = true;

    [Header("Debug readout (Play mode)")]
    [Tooltip("How far out the script thinks the slide is, 0 to 1. The kicker bar swings once this passes Start Fraction.")]
    [SerializeField] private float debugSlideFraction;
    [Tooltip("Kicker bar angle right now, degrees.")]
    [SerializeField] private float debugBarAngle;
    [Tooltip("Kicker bar angle the script is asking for, degrees.")]
    [SerializeField] private float debugBarTarget;

    [Header("Turret")]
    [SerializeField] private GenericTurret turret;
    [SerializeField] private PidConstants turretPID;

    [Header("Intake rollers")]
    [Tooltip("Intake rollers that spin the normal way.")]
    [SerializeField] private InteractionRoller[] intakeRollersForward;
    [Tooltip("Intake rollers that must spin the OPPOSITE way.")]
    [SerializeField] private InteractionRoller[] intakeRollersReversed;
    [Tooltip("Roller rpm while intaking.")]
    [SerializeField] private float intakeRpm = 2000f;
    [Tooltip("Roller rpm while outtaking (Robot Special).")]
    [SerializeField] private float outtakeRpm = -2000f;

    [Header("Dye rotor")]
    [Tooltip("Dye rotor roller(s) that spin the normal way. The big spinning hopper floor/drum.")]
    [SerializeField] private InteractionRoller[] dyeRotorForward;
    [Tooltip("Dye rotor roller(s) that must spin the OPPOSITE way.")]
    [SerializeField] private InteractionRoller[] dyeRotorReversed;
    [Tooltip("Dye rotor rpm while feeding the shooter. A big rotor should spin slowly.")]
    [SerializeField] private float dyeRotorShootRpm = 120f;
    [Tooltip("Dye rotor rpm while intaking, to stir fuel so it settles. 0 = stay still.")]
    [SerializeField] private float dyeRotorIntakeRpm = 0f;
    [Tooltip("Dye rotor rpm during Robot Special (unjam). Usually negative.")]
    [SerializeField] private float dyeRotorUnjamRpm = -80f;

    [Header("Kicker / feeder (optional)")]
    [Tooltip("Rollers that push fuel from the dye rotor up into the shooter, normal way.")]
    [SerializeField] private InteractionRoller[] kickerForward;
    [Tooltip("Kicker rollers that must spin the OPPOSITE way.")]
    [SerializeField] private InteractionRoller[] kickerReversed;
    [SerializeField] private float kickerRpm = 3000f;

    [Header("Shooter")]
    [Tooltip("Flywheel rollers that spin the normal way.")]
    [SerializeField] private InteractionRoller[] shooterForward;
    [Tooltip("Flywheel rollers that must spin the OPPOSITE way.")]
    [SerializeField] private InteractionRoller[] shooterReversed;
    [SerializeField] private float shooterRpm = 3000f;
    [Tooltip("Seconds the flywheel spins up before the dye rotor starts feeding.")]
    [SerializeField, Min(0f)] private float spinUpTime = 0.4f;
    [Tooltip("Only feed once the turret is aimed within the tolerance below. Leave off while testing.")]
    [SerializeField] private bool waitForTurret = false;
    [Tooltip("How close (degrees) the turret must be to its target when Wait For Turret is on.")]
    [SerializeField, Min(0f)] private float turretTolerance = 5f;

    [Header("Drive")]
    [SerializeField, Range(0f, 1f)] private float driveMultiplier = 1f;
    [SerializeField, Range(0f, 1f)] private float intakingDriveMultiplier = 1f;

    private bool _deployed;
    private float _targetInches;
    private float _shootStartTime = -1f;
    private Vector3 _slideStart;
    private Quaternion _barStowRotation;
    private float _barAngle;        // current swing, degrees (0 = folded)

    public bool IsDeployed => _deployed;

    /// <summary>How far the slide is actually out, 0 (stowed) to 1 (deployed).
    /// Measured in world meters, so a scaled parent (common with Blender imports) doesn't throw it off.</summary>
    public float ExtendedFraction
    {
        get
        {
            if (!intakeSlide || Mathf.Approximately(travelInches, 0f)) return 0f;
            Transform parent = intakeSlide.transform.parent;
            Vector3 now = intakeSlide.transform.position;
            Vector3 start = parent ? parent.TransformPoint(_slideStart) : _slideStart;
            return Vector3.Distance(now, start) / 0.0254f / Mathf.Abs(travelInches);
        }
    }

    private bool BarFolded => Mathf.Abs(_barAngle) < 3f;

    protected override void Init()
    {
        // Report missing references plainly instead of crashing Init.
        var missing = new List<string>();
        if (!intakeSlide) missing.Add(nameof(intakeSlide));
        if (!turret) missing.Add(nameof(turret));
        if (Count(intakeRollersForward, intakeRollersReversed) == 0) missing.Add("intake rollers");
        if (Count(dyeRotorForward, dyeRotorReversed) == 0) missing.Add("dye rotor rollers");
        if (Count(shooterForward, shooterReversed) == 0) missing.Add("shooter rollers");
        if (missing.Count > 0)
        {
            Debug.LogError($"{name}: unassigned {string.Join(", ", missing)} on TheBroncoBots. " +
                           "Right-click the component > Auto-find references, or drag them in.", this);
        }

        if (intakeSlide)
        {
            _slideStart = intakeSlide.transform.localPosition;
            intakeSlide.SetPid(intakePid);
        }
        if (kickerBarHinge) kickerBarHinge.useSpring = true;
        else if (kickerBar) _barStowRotation = kickerBar.localRotation;
        if (turret) turret.SetPid(turretPID);

        // Always call this, even if something above is missing, so the robot still finishes setting up.
        // It also registers the shooter handler that calls Shooting / Feed / Intaking / Idling below.
        base.Init();

        Scheduler.AddSubsystems(Intake, Drive);
        Scheduler.AddProtectedSystem(UpdateJoints);
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

    /// <summary>Feed() only runs while this is true.</summary>
    protected override bool Aligned()
    {
        bool spunUp = _shootStartTime >= 0f && Time.time - _shootStartTime >= spinUpTime;
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

    // ---------------------------------------------------------------------------------------- shooter
    // RebuiltTurretRobot calls exactly one of these every tick:
    //   Right trigger held      -> Shooting() (or IntakingAndShooting() if the intake is held too),
    //                              then Feed() once Aligned() is true
    //   Only left trigger held  -> Intaking()
    //   Nothing held            -> Idling()

    protected override void Shooting()
    {
        SpinUpShooter();
    }

    protected override void IntakingAndShooting()
    {
        SpinUpShooter();
    }

    protected override void Feed()
    {
        // Runs after Shooting(), so this overrides the "hold fuel back" speeds set there.
        Run(dyeRotorForward, dyeRotorReversed, dyeRotorShootRpm);
        Run(kickerForward, kickerReversed, kickerRpm);
    }

    protected override void Intaking()
    {
        StopShooter();
        if (!RobotSpecial) Run(dyeRotorForward, dyeRotorReversed, dyeRotorIntakeRpm);
    }

    protected override void Idling()
    {
        StopShooter();
    }

    private void SpinUpShooter()
    {
        if (_shootStartTime < 0f) _shootStartTime = Time.time;
        Run(shooterForward, shooterReversed, shooterRpm);

        // Hold fuel back until Feed() says the shot is ready.
        Run(dyeRotorForward, dyeRotorReversed, 0f);
        Run(kickerForward, kickerReversed, 0f);
    }

    private void StopShooter()
    {
        _shootStartTime = -1f;
        Run(shooterForward, shooterReversed, 0f);
        Run(kickerForward, kickerReversed, 0f);
        Run(dyeRotorForward, dyeRotorReversed, RobotSpecial ? dyeRotorUnjamRpm : 0f);
    }

    // ---------------------------------------------------------------------------------------- intake

    private void Intake()
    {
        if (IntakeAction || RobotSpecial) _deployed = true;      // latch out
        if (StowAction || RobotSpecial2) _deployed = false;      // bring in

        // While stowing, keep the slide out until the kicker bar has folded back.
        bool holdOut = !_deployed && foldBarBeforeRetract && (kickerBarHinge || kickerBar) && !BarFolded;
        _targetInches = _deployed || holdOut ? travelInches : 0f;

        if (RobotSpecial) Run(intakeRollersForward, intakeRollersReversed, outtakeRpm);
        else if (IntakeAction) Run(intakeRollersForward, intakeRollersReversed, intakeRpm);
        else Run(intakeRollersForward, intakeRollersReversed, 0f);
    }

    private void Drive()
    {
        if (driveController) driveController.SetDriveMp(IntakeAction ? intakingDriveMultiplier : driveMultiplier);
    }

    // 0 = stowed: the prefab starts with the slide in.
    private void UpdateJoints()
    {
        if (intakeSlide)
            intakeSlide.SetLinearTarget(_targetInches).withAxis(intakeAxis).useAutomaticStartingOffset();

        UpdateKickerBar();

        if (turret) turret.Align();
    }

    /// <summary>Swings the kicker bar out only once the slide is far enough out, and folds it otherwise.</summary>
    private void UpdateKickerBar()
    {
        bool swingOut = _deployed && ExtendedFraction >= kickerBarStartFraction;
        float target = swingOut ? kickerBarAngle : 0f;
        debugSlideFraction = ExtendedFraction;
        debugBarTarget = target;
        debugBarAngle = _barAngle;

        if (kickerBarHinge)
        {
            // Hinge angle 0 = where the bar sat when the game started (folded).
            var spring = kickerBarHinge.spring;
            spring.spring = kickerBarSpring;
            spring.damper = kickerBarDamper;
            spring.targetPosition = target;
            kickerBarHinge.spring = spring;
            _barAngle = kickerBarHinge.angle;
            return;
        }

        if (!kickerBar) return;
        _barAngle = Mathf.MoveTowards(_barAngle, target, kickerBarSpeed * Time.deltaTime);

        Vector3 axis = kickerBarAxis.sqrMagnitude > 0.0001f ? kickerBarAxis.normalized : Vector3.right;
        kickerBar.localRotation = _barStowRotation * Quaternion.AngleAxis(_barAngle, axis);
    }

    // Re-applied late so PID edits in the Inspector take effect during play.
    private void UpdatePids()
    {
        if (intakeSlide) intakeSlide.UpdatePid(intakePid);
        if (turret) turret.UpdatePid(turretPID);
    }

    // ---------------------------------------------------------------------------------------- rollers

    /// <summary>Spins the forward list at +rpm and the reversed list at -rpm. rpm 0 stops them.</summary>
    private static void Run(InteractionRoller[] forward, InteractionRoller[] reversed, float rpm)
    {
        Spin(forward, rpm);
        Spin(reversed, -rpm);
    }

    private static void Spin(InteractionRoller[] list, float rpm)
    {
        if (list == null) return;
        foreach (var r in list)
        {
            if (!r) continue;
            if (Mathf.Approximately(rpm, 0f)) r.Stop();
            else r.OverrideRpm(rpm);
        }
    }

    private static int Count(InteractionRoller[] a, InteractionRoller[] b)
    {
        int n = 0;
        if (a != null) foreach (var r in a) if (r) n++;
        if (b != null) foreach (var r in b) if (r) n++;
        return n;
    }

    // ---------------------------------------------------------------------------------------- editor setup

    private void Reset() => AutoFindReferences();

    /// <summary>Fills only empty fields, matching object names: "intake", "dye"/"rotor",
    /// "kick"/"feed"/"uptake", "shoot"/"flywheel". Everything found goes in the Forward lists;
    /// move any that spin the wrong way into the Reversed list.</summary>
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
        if (!kickerBarHinge && intakeSlide) kickerBarHinge = intakeSlide.GetComponentInChildren<HingeJoint>(true);
        if (!kickerBarHinge && !kickerBar && intakeSlide)
        {
            foreach (var t in intakeSlide.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("kickerbar") || n.Contains("kicker bar") || n.Contains("kicker_bar")) { kickerBar = t; break; }
            }
        }

        var intake = new List<InteractionRoller>();
        var rotor = new List<InteractionRoller>();
        var kicker = new List<InteractionRoller>();
        var shooter = new List<InteractionRoller>();
        foreach (var r in GetComponentsInChildren<InteractionRoller>(true))
        {
            string n = PathName(r.transform);
            if (n.Contains("dye") || n.Contains("rotor")) rotor.Add(r);
            else if (n.Contains("shoot") || n.Contains("flywheel")) shooter.Add(r);
            else if (n.Contains("intake")) intake.Add(r);   // checked before "kick" so kicker-bar rollers stay intake rollers
            else if (n.Contains("kick") || n.Contains("feed") || n.Contains("uptake")) kicker.Add(r);
        }
        if (Count(intakeRollersForward, intakeRollersReversed) == 0) intakeRollersForward = intake.ToArray();
        if (Count(dyeRotorForward, dyeRotorReversed) == 0) dyeRotorForward = rotor.ToArray();
        if (Count(kickerForward, kickerReversed) == 0) kickerForward = kicker.ToArray();
        if (Count(shooterForward, shooterReversed) == 0) shooterForward = shooter.ToArray();

        Debug.Log($"{name}: slide {Found(intakeSlide)}, kicker bar hinge {Found(kickerBarHinge)}, kicker bar {Found(kickerBar)}, turret {Found(turret)}, " +
                  $"{Count(intakeRollersForward, intakeRollersReversed)} intake rollers, " +
                  $"{Count(dyeRotorForward, dyeRotorReversed)} dye rotor rollers, " +
                  $"{Count(kickerForward, kickerReversed)} kicker rollers, " +
                  $"{Count(shooterForward, shooterReversed)} shooter rollers.", this);
    }

    /// <summary>The object's name plus its parents' names up to this robot, lowercase.</summary>
    private string PathName(Transform t)
    {
        string s = "";
        for (; t && t != transform; t = t.parent) s += t.name.ToLowerInvariant() + "/";
        return s;
    }

    private static string Found(Object o) => o ? o.name : "NOT FOUND";
}