using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class VerifyThirdPerson
{
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static List<string> results;
    static void Check(bool value,string label) { if(!value) throw new Exception("FAIL: "+label); results.Add("PASS: "+label); }
    public static string Main()
    {
        if(!Application.isPlaying) throw new Exception("Run in an offline Play session after RoomManager spawned the player");
        results=new List<string>();
        var motor=UnityEngine.Object.FindAnyObjectByType<NetworkThirdPersonController>();
        var setup=motor.GetComponent<PlayerSetup>();
        var cc=motor.GetComponent<CharacterController>();
        var weapon=motor.GetComponent<Weapon>();
        var orbit=weapon.camera.GetComponent<ThirdPersonOrbitCamera>();
        var move=typeof(NetworkThirdPersonController).GetMethod("Move",Flags);
        var vertical=typeof(NetworkThirdPersonController).GetField("verticalVelocity",Flags);
        var originalPosition=motor.transform.position;
        var originalRotation=motor.transform.rotation;
        var cameraPosition=weapon.camera.transform.position;
        var cameraRotation=weapon.camera.transform.rotation;
        var originalMuzzle=weapon._FirePoint;
        var originalAmmo=weapon.ammo;
        var originalMag=weapon.mag;
        var originalFacing=motor.faceCameraWhileAiming;
        var fixture=new GameObject("ThirdPersonVerificationFixture");
        motor.enabled=false; orbit.enabled=false; Cursor.lockState=CursorLockMode.None;
        var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.transform.SetParent(fixture.transform);
        floor.transform.position=new Vector3(0,199.5f,0);
        floor.transform.localScale=new Vector3(100,1,100);
        Action<Vector3> reset=pos=> {var camRot=weapon.camera.transform.rotation; cc.enabled=false; motor.transform.SetPositionAndRotation(pos,Quaternion.identity); cc.enabled=true; vertical.SetValue(motor,0f); weapon.camera.transform.rotation=camRot; Physics.SyncTransforms();};
        Action<Vector2,bool,bool,bool> step=(input,sprint,jump,aim)=>move.Invoke(motor,new object[]{input,sprint,jump,aim,1f/60f});
        Action settle=()=> {for(int i=0;i<30;i++) step(Vector2.zero,false,false,false);};
        Func<string,Vector3,Vector3,GameObject> box=(name,pos,size)=>{var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(fixture.transform);go.transform.position=pos;go.transform.localScale=size;return go;};
        try
        {
            weapon.camera.transform.rotation=Quaternion.identity;
            reset(new Vector3(0,200.1f,0)); settle();
            Check(cc.isGrounded,"Ground contact");
            for(int i=0;i<60;i++) step(Vector2.up,false,false,false);
            Check(Mathf.Abs(motor.transform.position.z-3f)<0.05f,"Walk 3 metres/second");
            reset(new Vector3(0,200.1f,0)); settle();
            for(int i=0;i<60;i++) step(Vector2.up,true,false,false);
            Check(Mathf.Abs(motor.transform.position.z-6f)<0.05f,"Run 6 metres/second");
            reset(new Vector3(0,200.1f,0)); settle();
            for(int i=0;i<60;i++) step(Vector2.one,false,false,false);
            Check(Mathf.Abs(new Vector2(motor.transform.position.x,motor.transform.position.z).magnitude-3f)<0.05f,"Diagonal speed clamped");
            weapon.camera.transform.rotation=Quaternion.Euler(0,90,0);
            reset(new Vector3(0,200.1f,0)); settle();
            for(int i=0;i<60;i++) step(Vector2.up,false,false,false);
            Check(Mathf.Abs(motor.transform.position.x-3f)<0.05f,"Camera-relative movement");
            motor.faceCameraWhileAiming=true;
            for(int i=0;i<60;i++) step(Vector2.zero,false,false,true);
            Check(Quaternion.Angle(motor.transform.rotation,Quaternion.Euler(0,90,0))<1f,"Stationary aiming faces camera yaw");
            reset(new Vector3(0,200.1f,0)); settle();
            float groundY=motor.transform.position.y,peak=groundY;
            for(int i=0;i<120;i++){step(Vector2.zero,false,i==0,false);peak=Mathf.Max(peak,motor.transform.position.y);}
            Check(Mathf.Abs(peak-groundY-1.4f)<0.12f && cc.isGrounded,"Jump height 1.4 and landing (peak="+(peak-groundY).ToString("F3")+")");
            weapon.camera.transform.rotation=Quaternion.identity;
            var wall=box("Wall",new Vector3(0,202,2),new Vector3(8,4,0.3f));
            reset(new Vector3(0,200.1f,0));settle();
            for(int i=0;i<100;i++)step(Vector2.up,false,false,false);
            Check(motor.transform.position.z<1.6f,"Character blocked by wall");
            wall.SetActive(false);
            var ramp=box("Ramp",new Vector3(0,200.8f,3),new Vector3(4,0.2f,6));ramp.transform.rotation=Quaternion.Euler(-20,0,0);
            reset(new Vector3(0,200.1f,-1));settle();
            for(int i=0;i<100;i++)step(Vector2.up,false,false,false);
            Check(motor.transform.position.y>201f,"Climbs 20-degree ramp");ramp.SetActive(false);
            var stairs=new List<GameObject>();
            for(int i=0;i<4;i++)stairs.Add(box("Step"+i,new Vector3(0,200f+(i+1)*0.1f,1f+i*0.7f),new Vector3(4,(i+1)*0.2f,0.75f)));
            reset(new Vector3(0,200.1f,0));settle();
            for(int i=0;i<65;i++)step(Vector2.up,false,false,false);
            Check(motor.transform.position.y>200.6f,"Climbs 0.2-metre steps");
            foreach(var stair in stairs)stair.SetActive(false);
            reset(new Vector3(0,200.1f,0));settle();
            var muzzle=new GameObject("ProbeMuzzle");muzzle.transform.SetParent(motor.transform,false);muzzle.transform.localPosition=new Vector3(0,1.2f,0.8f);weapon._FirePoint=muzzle;
            weapon.camera.transform.SetPositionAndRotation(new Vector3(0,201.28f,-5),Quaternion.identity);
            var target=box("ShotTarget",new Vector3(0,201.2f,10),new Vector3(2,3,0.5f));Physics.SyncTransforms();
            Check(weapon.TryGetShotHit(out var hit)&&hit.collider.gameObject==target,"Camera-to-muzzle shot ignores own controller");
            wall.transform.position=new Vector3(0,201.2f,0.4f);wall.SetActive(true);Physics.SyncTransforms();
            Check(weapon.TryGetShotHit(out hit)&&hit.collider.gameObject==wall,"Chest-to-muzzle obstruction prevents wall penetration");
            wall.transform.position=new Vector3(0,201.2f,3);Physics.SyncTransforms();
            Check(weapon.TryGetShotHit(out hit)&&hit.collider.gameObject==wall,"Muzzle-to-target obstruction hits nearest cover");
            wall.SetActive(false);target.SetActive(false);UnityEngine.Object.Destroy(muzzle);weapon._FirePoint=originalMuzzle;
            var resolve=typeof(ThirdPersonOrbitCamera).GetMethod("ResolveCollision",Flags);
            typeof(ThirdPersonOrbitCamera).GetField("orbitCamera",Flags).SetValue(orbit,weapon.camera);
            Vector3 pivot=motor.transform.position+Vector3.up*1.5f;
            int cases=0;
            foreach(float pitch in new[]{-25f,-15f,0f,15f,45f,70f})for(int yaw=0;yaw<360;yaw+=10)
            {
                Vector3 wanted=pivot+Quaternion.Euler(pitch,yaw,0)*Vector3.back*5f;
                Vector3 safe=(Vector3)resolve.Invoke(orbit,new object[]{pivot,wanted});
                if(safe.y<200.24f)throw new Exception("Camera penetrated floor");
                cases++;
            }
            Check(cases==216,"216 camera pitch/yaw floor collision cases");
            weapon.ammo=10;weapon.mag=5;
            Check(weapon.TryReload() && !weapon.TryFire() && weapon.ammo==10 && weapon.mag==5,"Reload blocks fire without granting ammo early");
            typeof(Weapon).GetField("reloadRemaining",Flags).SetValue(weapon,0f);
            typeof(Weapon).GetMethod("Update",Flags).Invoke(weapon,null);
            Check(!weapon.IsReloading && weapon.ammo==30 && weapon.mag==4 && weapon.ammoText.text=="30/30","Reload completion consumes one magazine and refreshes HUD");
            Check(!weapon.TryReload(),"Full magazine does not reload");
            weapon.ammo=0;Check(!weapon.TryFire(),"Empty magazine cannot fire");
            var apply=typeof(PlayerSetup).GetMethod("ApplyPresentation",Flags);
            apply.Invoke(setup,new object[]{false});
            Check(!weapon.enabled && !setup.personalHud.activeSelf && !weapon.camera.gameObject.activeSelf && motor.animator.gameObject.activeSelf,"Remote presentation keeps full body and disables input/camera/HUD");
            apply.Invoke(setup,new object[]{true});
            Check(weapon.enabled && setup.personalHud.activeSelf && weapon.camera.enabled,"Local presentation restores weapon/camera/HUD");
            setup.GetComponent<PlayerEffects>().SendMuzzleFlash();
            Check(setup.GetComponentsInChildren<ParticleSystem>().Length>0,"Muzzle RPC creates active effect on equipped weapon");
            Check(motor.leftHandIK!=null && motor.leftHandIK.IsValid(),"Left-hand grip IK references valid");
            string report=string.Join("\n",results);
            System.IO.File.WriteAllText("Temp/ThirdPersonVerification.txt",report);
            return report;
        }
        finally
        {
            weapon._FirePoint=originalMuzzle;weapon.ammo=originalAmmo;weapon.mag=originalMag;
            motor.faceCameraWhileAiming=originalFacing;
            typeof(Weapon).GetMethod("RefreshAmmoUI",Flags).Invoke(weapon,null);
            UnityEngine.Object.Destroy(fixture);
            cc.enabled=false;motor.transform.SetPositionAndRotation(originalPosition,originalRotation);cc.enabled=true;
            weapon.camera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);
            motor.enabled=true;orbit.enabled=true;
        }
    }
}
