using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ApplyFullbodyIKToArena
{
    static T Reference<T>(SerializedObject source, string name) where T : UnityEngine.Object => (T)source.FindProperty(name).objectReferenceValue;
    static void Pose(Transform from, Transform to)
    {
        to.localPosition=from.localPosition; to.localRotation=from.localRotation; to.localScale=from.localScale;
    }
    static Transform Remap(Transform from, Transform sourceVisual, Transform targetVisual)
    {
        if(from==null)return null;
        var path=AnimationUtility.CalculateTransformPath(from,sourceVisual);
        var result=targetVisual.Find(path);
        if(result==null)throw new Exception("Missing matching transform: "+path);
        return result;
    }
    static void Constraint(TwoBoneIKConstraint from, TwoBoneIKConstraint to, Transform sourceVisual, Transform targetVisual)
    {
        var data=from.data;
        data.root=Remap(data.root,sourceVisual,targetVisual);
        data.mid=Remap(data.mid,sourceVisual,targetVisual);
        data.tip=Remap(data.tip,sourceVisual,targetVisual);
        data.target=Remap(data.target,sourceVisual,targetVisual);
        data.hint=Remap(data.hint,sourceVisual,targetVisual);
        to.data=data;to.weight=0f;to.enabled=from.enabled;
        Pose(from.transform,to.transform);Pose(from.data.target,data.target);Pose(from.data.hint,data.hint);
        if(!to.IsValid())throw new Exception("Invalid IK "+to.name);
    }
    public static string Main()
    {
        if(Application.isPlaying)throw new Exception("Stop Play first");
        const string path="Assets/Resources/Player_ThirdPerson.prefab";
        var source=UnityEngine.Object.FindAnyObjectByType<EquipWeapon>();
        if(source==null || source.gameObject.scene.path!="Assets/Scenes/Fullbody_Test_Scene.unity")throw new Exception("Live Fullbody test scene required");
        var fields=new SerializedObject(source);
        var sourceVisual=source.GetComponentInChildren<Animator>().transform;
        var sourceRight=Reference<TwoBoneIKConstraint>(fields,"rightHandIK");
        var sourceLeft=Reference<TwoBoneIKConstraint>(fields,"leftHnadIK");
        var sourceAim=Reference<Transform>(fields,"aimingPos");
        var sourceHolder=Reference<Transform>(fields,"currentWeaponPos");
        var sourceGrip=Reference<Transform>(fields,"IKLeftHandPos");
        var stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory("Temp/ArenaIK");
        File.Copy(path,"Temp/ArenaIK/Player_ThirdPerson-before-"+stamp+".prefab");
        // Preserve the user's unsaved test-scene state without saving over its asset.
        if(!EditorSceneManager.SaveScene(source.gameObject.scene,"Temp/ArenaIK/Fullbody-live-"+stamp+".unity",true))throw new Exception("Scene snapshot failed");
        var root=PrefabUtility.LoadPrefabContents(path);
        try
        {
            var motor=root.GetComponent<NetworkThirdPersonController>();
            var visual=motor.animator.transform;
            Pose(sourceVisual,visual);
            var holder=Remap(sourceHolder,sourceVisual,visual);Pose(sourceHolder,holder);
            var weapon=holder.Find("Vector");
            if(weapon==null)throw new Exception("Equipped Vector missing");
            weapon.localPosition=Vector3.zero;weapon.localRotation=Quaternion.identity;
            weapon.localScale=sourceGrip.parent.localScale;
            var positions=visual.Find(sourceAim.parent.name);
            if(positions==null)positions=UnityEngine.Object.Instantiate(sourceAim.parent.gameObject,visual,false).transform;
            positions.name=sourceAim.parent.name;Pose(sourceAim.parent,positions);
            foreach(Transform control in sourceAim.parent)
            {
                var dest=positions.Find(control.name);
                if(dest==null){dest=new GameObject(control.name).transform;dest.SetParent(positions,false);}
                Pose(control,dest);
            }
            var grip=weapon.Find("L_Hand_Pos")??weapon.Find("LeftHandGrip");
            if(grip==null){grip=new GameObject("L_Hand_Pos").transform;grip.SetParent(weapon,false);}
            grip.name="L_Hand_Pos";Pose(sourceGrip,grip);
            var sourceRightGrip=Reference<Transform>(fields,"IKRightHandPos");
            var rightGrip=weapon.Find("R_Hand_Pos");
            if(rightGrip==null){rightGrip=new GameObject("R_Hand_Pos").transform;rightGrip.SetParent(weapon,false);}
            Pose(sourceRightGrip,rightGrip);
            var right=Remap(sourceRight.transform,sourceVisual,visual).GetComponent<TwoBoneIKConstraint>();
            var left=Remap(sourceLeft.transform,sourceVisual,visual).GetComponent<TwoBoneIKConstraint>();
            Constraint(sourceRight,right,sourceVisual,visual);Constraint(sourceLeft,left,sourceVisual,visual);
            var rig=right.GetComponentInParent<Rig>();rig.weight=sourceRight.GetComponentInParent<Rig>().weight;
            var ik=root.GetComponent<HandHeldWeaponIK>()??root.AddComponent<HandHeldWeaponIK>();
            ik.equippedWeapon=weapon;ik.aimingPos=positions.Find(sourceAim.name);ik.leftHandGrip=grip;
            // The live source's left target is 9 cm beyond the arm at full aim.
            // Keep its height/side alignment and bring the arena weapon within reach.
            var aimLocal=ik.aimingPos.localPosition;aimLocal.z=-.05f;ik.aimingPos.localPosition=aimLocal;
            ik.rightHandIK=right;ik.leftHandIK=left;
            motor.handIK=ik;motor.leftHandIK=left;
            if(ik.aimingPos.IsChildOf(right.data.tip)||left.data.target.IsChildOf(right.data.tip))throw new Exception("IK feedback hierarchy");
            var result=PrefabUtility.SaveAsPrefabAsset(root,path);
            if(result==null)throw new Exception("Prefab save failed");
            return "Applied live source aim="+sourceAim.localPosition.ToString("F4")+" with arena reach correction="+ik.aimingPos.localPosition.ToString("F4")+" and calibrated grips/hints; saved "+path+"; source dirty="+source.gameObject.scene.isDirty;
        }
        finally{PrefabUtility.UnloadPrefabContents(root);}
    }
}
