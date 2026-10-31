namespace AgentChecks.Editor
{
    public interface IProjectEditorCheck
    {
        string Name { get; }
        string Scope { get; }
        void Run(ProjectCheckResults results);
    }
}
