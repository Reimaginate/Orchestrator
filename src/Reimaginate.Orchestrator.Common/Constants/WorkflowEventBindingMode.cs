namespace Reimaginate.Orchestrator.Common.Constants;

[Flags]
public enum WorkflowEventBindingMode
{
    Start = 1,
    Resume = 2,
    StartOrResume = Start | Resume
}