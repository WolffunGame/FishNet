using System;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using UnityEngine;

// ReSharper disable UnusedParameter.Local
namespace FishNet.PredictionV2
{
    public class RigidBodyPv2 : NetworkBehaviour
    {
#if PREDICTION_V2

        [SerializeField] private float _jumpForce = 15f;
        [SerializeField] private float _moveRate = 15f;
        [SerializeField] private float _interval = 0.1f;
        private JumpState _jumpState;
        private float _curTime;

        private Rigidbody RigidBody { get; set; }
        private bool _jump;

        private void Update()
        {
            if (!IsOwner)
                return;

            if (Input.GetKeyDown(KeyCode.Space))
                _jump = true;
        }

        public override void OnStartNetwork()
        {
            RigidBody = GetComponent<Rigidbody>();
            TimeManager.OnTick += TimeManager_OnTick;
            TimeManager.OnPostTick += TimeManager_OnPostTick;
        }

        public override void OnStopNetwork()
        {
            TimeManager.OnTick -= TimeManager_OnTick;
            TimeManager.OnPostTick -= TimeManager_OnPostTick;
        }

        private void TimeManager_OnTick() => Move(BuildMoveData());

        private MoveData BuildMoveData()
        {
            if (!IsOwner && Owner.IsValid)
                return default;
            var horizontal = Input.GetAxisRaw("Horizontal");
            var vertical = Input.GetAxisRaw("Vertical");
            var md = new MoveData(_jump, horizontal, vertical);
            _jump = false;
            return md;
        }

        public uint LastMdTick;

        [Replicate]
        private void Move(MoveData md, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            if (state.IsPredicted() && !Owner.IsLocalClient)
                return;
            LastMdTick = md.GetTick();
            var forces = new Vector3(md.Horizontal, 0f, md.Vertical) * _moveRate;
            forces += Physics.gravity * 3f;
            _curTime -= (float)TimeManager.TickDelta;
            RigidBody.AddForce(forces);
            
            if (_jumpState is JumpState.None or JumpState.Prepare)
            {
                if(_curTime > 0f)
                 return;
            }
            
            switch (_jumpState)
            {
                case JumpState.None when md.Jump:
                    Jump();
                    return;
                case JumpState.Prepare:
                case JumpState.Jumping:
                default:
                    break;
            }
        }

        private void Jump()
        {
            _jumpState = JumpState.None;
            _curTime = _interval;
            RigidBody.AddForce(new Vector3(0f, _jumpForce, 0f), ForceMode.Impulse);
        }

        private void SendReconcile()
        {
            /* The base.IsServer check is not required but does save a little
             * performance by not building the reconcileData if not server. */
            if (!IsServerStarted)
                return;
            var tran = transform;
            var rd = new ReconcileData(tran.position, tran.rotation, RigidBody.velocity, RigidBody.angularVelocity,
                _curTime, (byte) _jumpState);
            Reconciliation(rd);
        }

        private void TimeManager_OnPostTick() => SendReconcile();

        [Reconcile]
        private void Reconciliation(ReconcileData rd, Channel channel = Channel.Unreliable)
        {
            var tran = transform;
            tran.position = rd.Position;
            tran.rotation = rd.Rotation;
            RigidBody.velocity = rd.Velocity;
            RigidBody.angularVelocity = rd.AngularVelocity;
            _curTime = rd.JumpInterval;
            _jumpState = (JumpState) rd.JumpState;
        }

        #region Data

        private struct MoveData : IReplicateData
        {
            public readonly bool Jump;
            public readonly float Horizontal;
            public readonly float Vertical;

            public MoveData(bool jump, float horizontal, float vertical)
            {
                Jump = jump;
                Horizontal = horizontal;
                Vertical = vertical;
                _tick = 0;
            }

            private uint _tick;

            public void Dispose()
            {
            }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        private struct ReconcileData : IReconcileData
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly Vector3 Velocity;
            public readonly Vector3 AngularVelocity;
            public readonly float JumpInterval;
            // ReSharper disable once MemberHidesStaticFromOuterClass
            public readonly byte JumpState;

            public ReconcileData(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity,
                float jumpInterval, byte jumpState)
            {
                Position = position;
                Rotation = rotation;
                Velocity = velocity;
                AngularVelocity = angularVelocity;
                JumpInterval = jumpInterval;
                JumpState = jumpState;
                _tick = 0;
            }

            private uint _tick;

            public void Dispose()
            {
            }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        #endregion

#endif

        private enum JumpState : byte
        {
            None,
            Prepare,
            Jumping,
        }
    }
}