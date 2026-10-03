using System;

namespace Golem
{
    /// <summary>The joint spec written by golem_cutter.py (*_joints.json), in glTF coordinates.</summary>
    [Serializable]
    public class GolemSpec
    {
        public string asset;
        public string frame;
        public GolemJoint[] joints;
    }

    [Serializable]
    public class GolemJoint
    {
        public string type;          // "revolute" or "prismatic"
        public string parent;        // glTF node name
        public string child;         // glTF node name
        public float[] pivot;        // glTF scene frame (right-handed, +Y up, front +Z)
        public float[] axis;         // glTF scene frame; positive rotation opens the part
        public float[] limits_deg;   // [lower, upper]
        public string hinge_side;
    }
}
