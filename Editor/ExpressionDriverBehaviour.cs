using UnityEngine;
using UnityEngine.Animations;

namespace VRVlog.LilToonExporter
{
    // Only attached to owned, temporary evaluation controllers. The four-argument
    // callback addresses the actual playable, not Animator.runtimeAnimatorController.
    internal sealed class ExpressionDriverBehaviour : StateMachineBehaviour
    {
        [SerializeField] internal int Session;
        [SerializeField] internal int Program;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex,
            AnimatorControllerPlayable controller)
        {
            ExpressionEvaluationSession.Enter(Session, Program, animator, controller);
        }
    }
}
