using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEditor;

public static class InspectArenaIK
{
    static string Path(Transform t) => t == null ? "null" : (t.parent == null ? t.name : Path(t.parent) + "/" + t.name);
    static void Describe(StringBuilder b, GameObject root)
    {
        b.AppendLine("ROOT " + Path(root.transform));
        foreach(var t in root.GetComponentsInChildren<Transform>(true).Where(t => t==root.transform || t.GetComponent<Animator>() || t.name.Contains("Hand") || t.name.Contains("Weapon") || t.name.Contains("Vector") || t.name.Contains("Grip") || t.name.Contains("Rig")))
            b.AppendLine(Path(t)+" p="+t.localPosition.ToString("F6")+" r="+t.localRotation.eulerAngles.ToString("F3")+" scale="+t.localScale+" world="+t.position+" components="+string.Join(",",t.GetComponents<Component>().Where(c=>c).Select(c=>c.GetType().Name)));
        foreach(var ik in root.GetComponentsInChildren<TwoBoneIKConstraint>(true))
            b.AppendLine(ik.name+": "+EditorJsonUtility.ToJson(ik)+" root="+Path(ik.data.root)+" tip="+Path(ik.data.tip)+" target="+Path(ik.data.target)+" hint="+Path(ik.data.hint));
        foreach(var a in root.GetComponentsInChildren<Animator>(true)) b.AppendLine("Animator: "+a.name+" controller="+AssetDatabase.GetAssetPath(a.runtimeAnimatorController));
        foreach(var equip in root.GetComponentsInChildren<EquipWeapon>(true))
        {
            var so=new SerializedObject(equip);var p=so.GetIterator();
            while(p.NextVisible(true))if(p.propertyType==SerializedPropertyType.ObjectReference)b.AppendLine("Equip "+p.propertyPath+"="+(p.objectReferenceValue is Component c?Path(c.transform):p.objectReferenceValue?.name));
        }
    }
    public static string Main()
    {
        var b=new StringBuilder();
        Describe(b,UnityEngine.Object.FindFirstObjectByType<ThirdPersonTestController>().gameObject);
        var attached=UnityEngine.Object.FindFirstObjectByType<AttachedWeapon>();Describe(b,attached.gameObject);
        var prefab=PrefabUtility.LoadPrefabContents("Assets/Resources/Player_ThirdPerson.prefab");
        try{Describe(b,prefab);}finally{PrefabUtility.UnloadPrefabContents(prefab);}
        System.IO.File.WriteAllText("Temp/ArenaIK-inspection.txt",b.ToString());
        return b.ToString();
    }
}
