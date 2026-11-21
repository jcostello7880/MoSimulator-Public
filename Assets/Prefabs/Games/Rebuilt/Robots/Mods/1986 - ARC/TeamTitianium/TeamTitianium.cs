using Games.Rebuilt.Robots;
using RobotFramework.Components;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using UnityEngine;
public class MyRobot : RebuiltRobotBase
{
 [Header("Intake (rack and pinion)")]
 [SerializeField] private GenericJoint intakeSlide;
 [SerializeField] private JointAxis intakeAxis = JointAxis.Z;
 [SerializeField] private RollerGroup intakeRollers;
 [SerializeField] private PidConstants intakePid = new PidConstants(4, 0, 0.0005f, 0.6f, 0);
 [Tooltip("Slide travel from stowed to deployed, inches.")]
 [SerializeField] private float intakeExtendInches = 12f;
 [Header("Scoop")]
 [SerializeField] private Collider[] intakeRampColliders;
 [SerializeField, Range(0f, 1f)] private float intakeRampMinFraction = 0.8f;
 [Header("Agitate while shooting")]
 [SerializeField] private bool agitateWhileShooting = true;
 [SerializeField] private float agitateMinInches = 0f;
 [SerializeField] private float agitateMaxInches = 7f;
 [SerializeField, Min(0.05f)] private float agitatePeriod = 0.6f;
 [SerializeField] private float intakeShootRpm = 2000f;
 private bool _deployed, _agitating, _rampEnabled;
 private float _targetInches, _agitateTime;
 private Vector3 _slideStart;
 private bool WantsToShoot => OuttakeAction || AutoPass || ManualShoot;
 protected override void Init()
 {
 if (!intakeSlide || !intakeRollers)
 {
 Debug.LogError("MyRobot: intakeSlide or intakeRollers is not assigned.", this);
 return; // a throw here would hide the real error behind FixedUpdate exceptions
 }
 _slideStart = intakeSlide.transform.localPosition;
 intakeSlide.SetPid(intakePid);
 int field = LayerMask.GetMask("Field");
 if (intakeRampColliders != null)
 foreach (var c in intakeRampColliders) if (c) c.excludeLayers |= field;
 SetRamp(false);
 Scheduler.AddSubsystems(Intake);
 Scheduler.AddProtectedSystem(UpdateJoints);
 Scheduler.AddLateRecursive(UpdatePids);
 }
private void Intake()
 {
 // Latch: Shift deploys; Stow or Robot Special 2 brings it in.
 if (IntakeAction || (RobotSpecial && !WantsToShoot)) _deployed = true;
 if (StowAction || RobotSpecial2) _deployed = false;
 // Agitation while shooting: smooth swing between min and max, starting out.
 // Holding Shift keeps the intake out instead.
 bool agitate = agitateWhileShooting && WantsToShoot && !IntakeAction;
 if (agitate && !_agitating) _agitateTime = 0f; // rising edge: start "out"
 _agitating = agitate;
 if (_agitating)
 {
 _agitateTime += Time.fixedDeltaTime;
 float phase = 0.5f + 0.5f * Mathf.Cos(2f * Mathf.PI * _agitateTime / agitatePeriod);
 _targetInches = Mathf.Lerp(agitateMinInches, agitateMaxInches, phase);
 }
 else
 {
 _targetInches = _deployed ? intakeExtendInches : 0f;
 }
 // Scoop solid only when the slide is really out.
 float extended =
 Vector3.Distance(intakeSlide.transform.localPosition, _slideStart) / 0.0254f;
 SetRamp(extended >= intakeRampMinFraction * intakeExtendInches);
 // Roller: every state commanded explicitly.
 if (RobotSpecial) intakeRollers.FlipRpm(); // outtake
 else if (WantsToShoot) intakeRollers.OverrideRpm(intakeShootRpm);
 else if (!IntakeAction) intakeRollers.Stop();
 // IntakeAction: no call -> the roller's own rpm
 }
 private void UpdateJoints() =>
 intakeSlide.SetLinearTarget(_targetInches)
 .withAxis(intakeAxis)
 .useAutomaticStartingOffset();
 // re-applied late so inspector edits during play take effect
 private void UpdatePids() => intakeSlide.UpdatePid(intakePid);
 private void SetRamp(bool on)
 {
 if (on == _rampEnabled || intakeRampColliders == null) return;
 _rampEnabled = on;
 foreach (var c in intakeRampColliders) if (c) c.enabled = on;
 }
}