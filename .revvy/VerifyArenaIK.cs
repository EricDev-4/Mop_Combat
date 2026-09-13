using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEditor;
using Photon.Pun;

public static class VerifyArenaIK
{
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void State(NetworkThirdPersonController motor, bool aim, float speed=0f)
    {
        motor.OnPhotonSerializeView(new PhotonStream(false,new object[]{speed,true,aim}),default(PhotonMessageInfo));
    }
    static async Task Step(int frames)
    {
        for(int i=0;i<frames;i++){EditorApplication.Step();await Task.Delay(100);if(!Application.isPlaying)throw new Exception("Play stopped");}
    }
    public static async Task<string> Probe()
    {
        if(!Application.isPlaying)throw new Exception("Play required");
        EditorApplication.isPaused=true;
        var m=UnityEngine.Object.FindAnyObjectByType<NetworkThirdPersonController>();
        m.ConfigureOwnership(false);m.weapon.enabled=false;
        m.cameraTransform.GetComponent<ThirdPersonOrbitCamera>().enabled=false;
        m.GetComponent<CharacterController>().enabled=false;
        State(m,true);m.animator.SetLayerWeight(1,1f);
        typeof(NetworkThirdPersonController).GetField("aimWeight",Flags).SetValue(m,1f);
        await Step(8);
        var ik=m.handIK;
        Func<TwoBoneIKConstraint,string> desc=c=>c.name+": targetGap="+Vector3.Distance(c.data.tip.position,c.data.target.position).ToString("F5")+" reach="+Vector3.Distance(c.data.root.position,c.data.target.position).ToString("F4")+" length="+(Vector3.Distance(c.data.root.position,c.data.mid.position)+Vector3.Distance(c.data.mid.position,c.data.tip.position)).ToString("F4");
        return "weaponGap="+Vector3.Distance(ik.equippedWeapon.position,ik.aimingPos.position).ToString("F5")+" angle="+Quaternion.Angle(ik.equippedWeapon.rotation,ik.aimingPos.rotation)+"; "+desc(ik.rightHandIK)+"; "+desc(ik.leftHandIK);
    }
    public static async Task<string> Main()
    {
        if(!Application.isPlaying)throw new Exception("Play required");
        EditorApplication.isPaused=true;
        var m=UnityEngine.Object.FindAnyObjectByType<NetworkThirdPersonController>();
        if(m==null||m.gameObject.scene.path!="Assets/Scenes/SplashPark_Arena.unity")throw new Exception("Arena spawned player required");
        var ik=m.handIK;var weapon=ik.equippedWeapon;var holder=weapon.parent;
        var originalPosition=m.transform.position;var originalRotation=m.transform.rotation;
        var aimPosition=ik.aimingPos.localPosition;var aimRotation=ik.aimingPos.localRotation;
        var localPosition=weapon.localPosition;var localRotation=weapon.localRotation;var localScale=weapon.localScale;
        var cameraPosition=m.cameraTransform.position;var cameraRotation=m.cameraTransform.rotation;
        m.ConfigureOwnership(false);m.weapon.enabled=false;m.GetComponent<CharacterController>().enabled=false;
        m.cameraTransform.GetComponent<ThirdPersonOrbitCamera>().enabled=false;
        typeof(NetworkThirdPersonController).GetField("aimWeight",Flags).SetValue(m,0f);
        m.animator.SetLayerWeight(1,0f);
        int frames=0;float gunGap=0,leftGap=0,gunAngle=0,rotationDrift=0,equipMotion=0;
        var detail=new System.Text.StringBuilder();
        try
        {
            for(int phase=0;phase<6;phase++)
            {
                bool aiming=phase>=1&&phase<=4;
                if(phase==2){ik.aimingPos.localPosition=aimPosition+new Vector3(.01f,.01f,-.02f);ik.aimingPos.localRotation=aimRotation*Quaternion.Euler(-3,5,0);}
                if(phase==3){m.transform.SetPositionAndRotation(originalPosition+new Vector3(1,0,1),originalRotation*Quaternion.Euler(0,70,0));}
                if(phase==4)m.animator.speed=0f;
                else m.animator.speed=1f;
                State(m,aiming,phase==3?.5f:0f);
                var initialGunRotation=weapon.rotation;
                float phaseLeft=0f,phaseTarget=0f,phaseReach=0f,phasePredicted=0f;
                for(int i=0;i<(phase==1||phase==5?72:12);i++)
                {
                    await Step(1);frames++;
                    if(weapon.parent!=holder||Vector3.Distance(weapon.localPosition,localPosition)>.00001f||Quaternion.Angle(weapon.localRotation,localRotation)>.01f||weapon.localScale!=localScale)throw new Exception("Attachment drift");
                    float expected=m.animator.GetLayerWeight(1);
                    if(Mathf.Abs(ik.rightHandIK.weight-expected)>.001f||Mathf.Abs(ik.leftHandIK.weight-expected)>.001f)throw new Exception("IK and animation blend weights differ");
                    if(aiming && expected>=.9999f)
                    {
                        gunGap=Mathf.Max(gunGap,Vector3.Distance(weapon.position,ik.aimingPos.position));
                        gunAngle=Mathf.Max(gunAngle,Quaternion.Angle(weapon.rotation,ik.aimingPos.rotation));
                        leftGap=Mathf.Max(leftGap,Vector3.Distance(ik.leftHandIK.data.tip.position,ik.leftHandGrip.position));
                        phaseLeft=Mathf.Max(phaseLeft,Vector3.Distance(ik.leftHandIK.data.tip.position,ik.leftHandGrip.position));
                        phaseTarget=Mathf.Max(phaseTarget,Vector3.Distance(ik.leftHandIK.data.tip.position,ik.leftHandIK.data.target.position));
                        phaseReach=Mathf.Max(phaseReach,Vector3.Distance(ik.leftHandIK.data.root.position,ik.leftHandIK.data.target.position));
                        phasePredicted=Mathf.Max(phasePredicted,Vector3.Distance(ik.leftHandGrip.position,ik.leftHandIK.data.target.position));
                    }
                    if(phase==0)equipMotion=Mathf.Max(equipMotion,Quaternion.Angle(initialGunRotation,weapon.rotation));
                    if(phase==4)rotationDrift=Mathf.Max(rotationDrift,Quaternion.Angle(initialGunRotation,weapon.rotation));
                }
                detail.AppendLine($"phase={phase} left={phaseLeft:F6} tipTarget={phaseTarget:F6} reach={phaseReach:F6} predictedGripGap={phasePredicted:F6} weight={m.animator.GetLayerWeight(1):F3}");
            }
            System.IO.File.WriteAllText("Temp/ArenaIK/Phases.txt",detail.ToString());
            if(gunGap>.003f||gunAngle>.2f||leftGap>.003f||rotationDrift>.1f||equipMotion<.1f)throw new Exception($"gunGap={gunGap:F6}, gunAngle={gunAngle:F3}, leftGap={leftGap:F6}, frozenDrift={rotationDrift:F3}, equipMotion={equipMotion:F3}");
            if(ik.rightHandIK.weight!=0f||ik.leftHandIK.weight!=0f)throw new Exception("Equip return did not release IK");
            // Exercise the actual serialized payload, then the remote controller Update.
            State(m,true,.5f);var outgoing=new PhotonStream(true,null);
            typeof(PhotonStream).GetMethod("SetWriteStream",Flags).Invoke(outgoing,new object[]{new System.Collections.Generic.List<object>(),0});
            m.OnPhotonSerializeView(outgoing,default(PhotonMessageInfo));
            var remote=UnityEngine.Object.Instantiate(Resources.Load<GameObject>("Player_ThirdPerson"),originalPosition+Vector3.right*3,originalRotation);
            try
            {
                var remoteMotor=remote.GetComponent<NetworkThirdPersonController>();
                await Step(1);remoteMotor.ConfigureOwnership(false);
                remoteMotor.OnPhotonSerializeView(new PhotonStream(false,outgoing.ToArray()),default(PhotonMessageInfo));
                await Step(72);
                if(remoteMotor.IsLocal||!remoteMotor.Aiming||remoteMotor.handIK.rightHandIK.weight!=1||remoteMotor.handIK.leftHandIK.weight!=1||remoteMotor.weapon.enabled||remoteMotor.cameraTransform.gameObject.activeSelf||remoteMotor.GetComponent<PlayerSetup>().personalHud.activeSelf)throw new Exception("Remote pose/ownership failure");
                if(Vector3.Distance(remoteMotor.handIK.equippedWeapon.position,remoteMotor.handIK.aimingPos.position)>.003f)throw new Exception("Remote aim mismatch");
            }
            finally{UnityEngine.Object.Destroy(remote);}
            var result=$"PASS {frames} production Update/rig frames: equip, aim, moved/rotated aim, player translation/rotation+walk, frozen animation, equip return. gunGap={gunGap:F6}m, gunAngle={gunAngle:F3}deg, leftGripGap={leftGap:F6}m, frozenDrift={rotationDrift:F3}deg, equipMotion={equipMotion:F3}deg. WeaponHolder/local TRS stable. Photon stream roundtrip -> remote full-body IK passed; remote camera/HUD/input ownership preserved.";
            System.IO.File.WriteAllText("Temp/ArenaIK/Verification.txt",result);return result;
        }
        finally
        {
            if(Application.isPlaying&&m)
            {
                m.animator.speed=1;m.transform.SetPositionAndRotation(originalPosition,originalRotation);
                ik.aimingPos.SetLocalPositionAndRotation(aimPosition,aimRotation);
                m.cameraTransform.SetPositionAndRotation(cameraPosition,cameraRotation);
                State(m,true);m.ConfigureOwnership(false);
            }
        }
    }
}
