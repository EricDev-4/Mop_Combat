using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class ConfigureThirdPersonAnimator
{
    static AnimationClip Clip(string path) => AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview"));
    public static string Main()
    {
        string path="Assets/05.Animation/Animator/Player_ThirdPerson.controller";
        if (!AssetDatabase.CopyAsset("Assets/05.Animation/Animator/Frog_FullBody.controller",path)) throw new Exception("Copy failed");
        var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        controller.AddParameter("MoveSpeed",AnimatorControllerParameterType.Float);
        controller.AddParameter("Grounded",AnimatorControllerParameterType.Bool);
        var lower=controller.layers[0].stateMachine;
        var state=lower.states[0].state;
        var walk=state.motion;
        state.name="Locomotion";
        var blend=new BlendTree {name="Idle Walk Run", blendType=BlendTreeType.Simple1D,blendParameter="MoveSpeed",useAutomaticThresholds=false};
        AssetDatabase.AddObjectToAsset(blend,controller);
        blend.AddChild(Clip("Assets/05.Animation/Clips/Male/Idles/HumanM@Idle01.fbx"),0f);
        blend.AddChild(walk,0.5f);
        blend.AddChild(Clip("Assets/05.Animation/Clips/Male/Movement/Run/HumanM@Run01_Forward.fbx"),1f);
        state.motion=blend;
        var air=lower.AddState("Airborne");
        air.motion=Clip("Assets/05.Animation/Clips/Male/Movement/Jump/HumanM@Fall01.fbx");
        var takeoff=state.AddTransition(air);
        takeoff.hasExitTime=false; takeoff.duration=0.1f;
        takeoff.AddCondition(AnimatorConditionMode.IfNot,0f,"Grounded");
        var land=air.AddTransition(state);
        land.hasExitTime=false; land.duration=0.1f;
        land.AddCondition(AnimatorConditionMode.If,0f,"Grounded");
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        var root=PrefabUtility.LoadPrefabContents("Assets/Resources/Player_ThirdPerson.prefab");
        try
        {
            root.GetComponent<NetworkThirdPersonController>().animator.runtimeAnimatorController=controller;
            PrefabUtility.SaveAsPrefabAsset(root,"Assets/Resources/Player_ThirdPerson.prefab");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return "Created isolated locomotion controller, preserved test controller and upper-body aim layer";
    }
}
