using UnityEngine;

namespace AgentChecks.Editor
{
    public interface IHierarchyLabelProvider
    {
        bool TryLabel(Component component, out string label);
    }
}
