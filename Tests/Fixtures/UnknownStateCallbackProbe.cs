using UnityEngine;

namespace VRVlog.LilToonExporter.Tests
{
    // Arbitrary authored code has no SDK data-only effect contract. Dependency
    // analysis must not assume this callback cannot alter its own gate input.
    public sealed class UnknownStateCallbackProbe : StateMachineBehaviour
    {
        public string Parameter = "AFK";

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (animator != null) animator.SetBool(Parameter, true);
        }
    }
}
