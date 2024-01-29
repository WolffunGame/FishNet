using FishNet.Object;
using UnityEngine;

namespace FishNet.PredictionV2
{
    public class SpringBoard : NetworkBehaviour
    {
#if PREDICTION_V2

        public float Force = 20f;

        private void OnTriggerEnter(Collider other)
        {
            if (other.TryGetComponent(out Rigidbody rigid))
                rigid.AddForce(Vector3.left * Force, ForceMode.Impulse);
        }

#endif
    }

}

public class PredictionRigidBody
{
    // ReSharper disable once MemberCanBePrivate.Global
    public Rigidbody RigidBody { get; private set; }

    private Vector3 _impulse;
    private Vector3 _force;
    public PredictionRigidBody(Rigidbody rb) => RigidBody = rb;


    public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force, bool afterSimulation = false)
    {
        if (afterSimulation)
        {
            _impulse += force;
            return;
        }

        if (_impulse != Vector3.zero)
        {
            RigidBody.AddForce(_impulse, ForceMode.Impulse);
            _impulse = Vector3.zero;
        }

        RigidBody.AddForce(force, mode);
    }
}