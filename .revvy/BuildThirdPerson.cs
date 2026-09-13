using System;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Photon.Pun;
using UnityEngine.UI;
using UnityEngine.Rendering.Universal;

public static class BuildThirdPerson
{
    static void Ref(UnityEngine.Object obj, string name, UnityEngine.Object value)
    {
        var so = new SerializedObject(obj);
        var prop = so.FindProperty(name);
        if (prop == null) throw new Exception("Missing field: " + name);
        prop.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
    public static string Main()
    {
        const string output = "Assets/Resources/Player_ThirdPerson.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(output) != null) throw new Exception("Output already exists");
        var source = UnityEngine.Object.FindFirstObjectByType<ThirdPersonTestController>();
        var sourceCamera = UnityEngine.Object.FindFirstObjectByType<ThirdPersonOrbitCamera>();
        if (source == null || sourceCamera == null) throw new Exception("Open Fullbody_Test_Scene first");
        var root = PrefabUtility.LoadPrefabContents("Assets/Resources/Player.prefab");
        try
        {
            root.name = "Player_ThirdPerson";
            var oldWeapon = root.GetComponentInChildren<Weapon>(true);
            var weapon = root.AddComponent<Weapon>();
            EditorUtility.CopySerialized(oldWeapon, weapon);
            var hud = root.GetComponentInChildren<Canvas>(true);
            hud.transform.SetParent(root.transform, false);
            hud.name = "PersonalHUD";
            hud.renderMode = RenderMode.ScreenSpaceOverlay;
            hud.worldCamera = null;
            var crosshair = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
            crosshair.transform.SetParent(hud.transform, false);
            var rect = (RectTransform)crosshair.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(4f, 4f);
            crosshair.GetComponent<Image>().raycastTarget = false;
            UnityEngine.Object.DestroyImmediate(root.transform.Find("CameraRoot").gameObject);
            UnityEngine.Object.DestroyImmediate(root.transform.Find("ThirdPersonVisual").gameObject);
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Movement>());
            UnityEngine.Object.DestroyImmediate(root.GetComponent<PhotonRigidbodyView>());
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Rigidbody>());
            foreach (var collider in root.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
            UnityEngine.Object.DestroyImmediate(root.GetComponent<MeshRenderer>());
            UnityEngine.Object.DestroyImmediate(root.GetComponent<MeshFilter>());
            var cc = root.AddComponent<CharacterController>();
            EditorUtility.CopySerialized(source.GetComponent<CharacterController>(), cc);
            var visual = UnityEngine.Object.Instantiate(source.GetComponentInChildren<Animator>().gameObject, root.transform, false);
            visual.name = "ThirdPersonVisual";
            foreach (var equip in visual.GetComponentsInChildren<EquipWeapon>(true)) UnityEngine.Object.DestroyImmediate(equip);
            foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            var camObject = UnityEngine.Object.Instantiate(sourceCamera.gameObject, root.transform, false);
            camObject.name = "ThirdPersonCamera";
            var cam = camObject.GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.cullingMask = ~(1 << LayerMask.NameToLayer("UI"));
            cam.nearClipPlane = 0.1f;
            camObject.GetComponent<ThirdPersonOrbitCamera>().SetTarget(root.transform);
            camObject.transform.localPosition = new Vector3(0f, 2.794f, -4.83f);
            camObject.transform.localRotation = Quaternion.Euler(15f, 0f, 0f);
            if (camObject.GetComponent<AudioListener>() == null) camObject.AddComponent<AudioListener>();
            var cameraData = cam.GetUniversalAdditionalCameraData();
            cameraData.renderType = CameraRenderType.Base;
            cameraData.cameraStack.Clear();
            var muzzleBone = visual.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Muzzle_DEF");
            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(muzzleBone, false);
            muzzle.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            weapon.thirdPerson = true;
            weapon.camera = cam;
            weapon._FirePoint = muzzle.gameObject;
            weapon._ADSPoint = null;
            weapon._animator = null;
            weapon.enabled = false;
            Ref(weapon, "adsPivot", null);
            Ref(weapon, "adsTarget", null);
            Ref(weapon, "weaponCam", null);
            Ref(weapon, "_weaponSetup", null);
            var controller = root.AddComponent<NetworkThirdPersonController>();
            controller.animator = visual.GetComponent<Animator>();
            controller.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            controller.cameraTransform = cam.transform;
            controller.weapon = weapon;
            var setup = root.GetComponent<PlayerSetup>();
            setup.thirdPersonController = controller;
            setup.personalHud = hud.gameObject;
            Ref(setup, "movement", null);
            Ref(setup, "playerCamera", camObject);
            Ref(setup, "thirdPersonVisual", visual);
            var effects = root.GetComponent<PlayerEffects>();
            effects.thirdPerson = true;
            Ref(effects, "firstPersonMuzzle", null);
            Ref(effects, "thirdPersonMuzzle", muzzle);
            var transformView = root.GetComponent<PhotonTransformView>();
            transformView.m_SynchronizePosition = true;
            transformView.m_SynchronizeRotation = true;
            transformView.m_UseLocal = false;
            var view = root.GetComponent<PhotonView>();
            view.ObservedComponents = new System.Collections.Generic.List<Component> { transformView, controller };
            camObject.SetActive(false);
            hud.gameObject.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(root, output);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        var main = EditorSceneManager.OpenScene("Assets/Scenes/SplashPark_Arena.unity", OpenSceneMode.Single);
        var room = UnityEngine.Object.FindFirstObjectByType<RoomManager>();
        if (room == null) throw new Exception("RoomManager missing");
        room.playerPb = AssetDatabase.LoadAssetAtPath<GameObject>(output);
        EditorUtility.SetDirty(room);
        EditorSceneManager.MarkSceneDirty(main);
        EditorSceneManager.SaveScene(main);
        AssetDatabase.SaveAssets();
        return output + " created; SplashPark_Arena RoomManager updated";
    }
}
