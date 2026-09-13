using UnityEngine;

// The ordered list of stages for one scenario run - the ScenarioDatabase-style
// container for PatientStageDefinition assets. Drop stage assets in here in order;
// reordering or adding a stage later is a data change, not a code change.
[CreateAssetMenu(fileName = "PatientStageSequence", menuName = "Dental Simulator/Patient Stage Sequence")]
public class PatientStageSequence : ScriptableObject
{
    public PatientStageDefinition[] stages;
}
