using Photon.Pun;
using TMPro;
using UnityEngine;

public class Weapon : MonoBehaviour
{
    public int damage;

    public Camera camera;
    [Min(0.01f)]
    public float fireRate = 10f;
    
    private float nextFire;

    [Header(("Ammo"))] 
    public int mag = 5;

    public int ammo = 30;
    public int magAmmo = 30;

    [Header("UI")] 
    public TextMeshProUGUI magText;
    public TextMeshProUGUI ammoText;
    
    public Animator _animator;

    private const int BaseLayer = 0;
    private const string ReloadStateName = "Base Layer.Reload";
    private static readonly int ReloadTrigger = Animator.StringToHash("Reload");
    
    [Header("VFX")]
    public GameObject muzzleFlash;
    public GameObject hitEffect;
    public GameObject _FirePoint;
    private PlayerEffects _playerEffects;

    [Header("ADS")] 
    public GameObject _ADSPoint;    // 총의 자식으로, 해당 총의 실제 조준점 위치와 회전을 나타낸다.
    [SerializeField] private Transform adsPivot;  // ADS 정렬을 위해 총 전체를 이동·회전시키는 부모 피벗이다.
    [SerializeField] private Transform adsTarget; // 조준점을 맞출 카메라 중앙의 고정된 목표 위치와 회전이다.
    [SerializeField] private Camera weaponCam;
    [Range(0 , 15)]
    [SerializeField] private float adsSpeed = 8f;

    [SerializeField, Range(1f, 179f)] private float adsFov = 50f;
    private float originFov;
    private Vector3 originAimPos;
    private Quaternion originAimQuaternion;
    public bool isAiming = false;

    private Recoil _recoil;
    [SerializeField] private WeaponSetup _weaponSetup;
    
    private void Start()
    {
        if(_animator == null)
            _animator = GetComponentInChildren<Animator>();
      _playerEffects = GetComponentInParent<PlayerEffects>();
      _recoil = GetComponentInParent<Recoil>();
      
        originAimPos = adsPivot.localPosition;
        originAimQuaternion = adsPivot.localRotation;
        originFov = weaponCam.fieldOfView;

        if (adsFov <= 1f)
            adsFov = Mathf.Max(1f, originFov - 15f);
        magText.text = mag.ToString();
        ammoText.text = ammo + "/" + magAmmo;
    }
    
    private void Update()
    {
        if(nextFire > 0)
            nextFire -= Time.deltaTime;

        bool isReloading = IsReloading();

        if (Input.GetButton("Fire1") && nextFire <= 0 && ammo > 0 && !isReloading)
        {
            nextFire = 1 / fireRate;
            
            Fire();
        }
        if (Input.GetKeyDown(KeyCode.R) && !isReloading && mag > 0)
        {
            Reload();
            _animator.SetTrigger(ReloadTrigger);
        }

        if (Input.GetMouseButtonDown(1))
            isAiming = !isAiming;
        
        UpdateADS();
    }

    private void UpdateADS()
    {
        float t = 1f - Mathf.Exp(-adsSpeed * Time.deltaTime);

        if (isAiming)
        {
            // ADSPos is part of the moving weapon hierarchy. Move the pivot by
            // the remaining sight-to-target offset instead of moving it directly
            // to ADSPos, which would make the target move every frame.
            Vector3 targetPosition =
                adsPivot.position +
                (adsTarget.position - _ADSPoint.transform.position);

            adsPivot.position = Vector3.Lerp(
                adsPivot.position,
                targetPosition,
                t
            );
        }
        else
        {
            adsPivot.localPosition = Vector3.Lerp(
                adsPivot.localPosition,
                originAimPos,
                t
            );

            adsPivot.localRotation = Quaternion.Slerp(
                adsPivot.localRotation,
                originAimQuaternion,
                t
            );
        }

        float targetFov = isAiming ? adsFov : originFov;

        weaponCam.fieldOfView = Mathf.Lerp(
            weaponCam.fieldOfView,
            targetFov,
            t
        );
    }

    private bool IsReloading()
    {
        if (_animator == null)
            return false;

        AnimatorStateInfo currentState = _animator.GetCurrentAnimatorStateInfo(BaseLayer);

        if (currentState.IsName(ReloadStateName))
            return true;

        if (!_animator.IsInTransition(BaseLayer))
            return false;

        AnimatorStateInfo nextState = _animator.GetNextAnimatorStateInfo(BaseLayer);
        return nextState.IsName(ReloadStateName);
    }

    void Reload()
    {
        if (mag > 0)
        {
            mag--;

            ammo = magAmmo;
        }
        magText.text = mag.ToString();
        ammoText.text = ammo + "/" + magAmmo;
    }

    private void Fire()
    {
        _recoil.RecoilFire();
        ammo--;
        magText.text = mag.ToString();
        ammoText.text = ammo + "/" + magAmmo;
        Ray ray = new Ray(camera.transform.position, camera.transform.forward);

        RaycastHit hit;
        _playerEffects.SendMuzzleFlash();
        if(Physics.Raycast(ray.origin, ray.direction, out hit, 100f))
        {
            if(hit.transform.gameObject.GetComponent<Health>())
                hit.transform.gameObject.GetComponent<PhotonView>().RPC("TakeDamage", RpcTarget.All, damage);
            _playerEffects.SendHitEffect(hit.point);
        }
    }

    public void PlayMuzzleFlash()
    {
        if (muzzleFlash == null || _FirePoint == null)
            return;
        
        GameObject effect = Instantiate(
            muzzleFlash,
            _FirePoint.transform.position,
            _FirePoint.transform.rotation,
            _FirePoint.transform);

        // World 좌표 파티클은 반동 중 이전 위치에 잔상이 남으므로
        // 모든 파티클이 총구의 움직임을 함께 따라가도록 한다.
        foreach (ParticleSystem particle in effect.GetComponentsInChildren<ParticleSystem>(true))
        {
            ParticleSystem.MainModule main = particle.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }
        
        Destroy(effect , 1f);
    }

    public void PlayHitEffect(Vector3 point)
    {
        if (hitEffect == null || point == null)
            return;
        
        GameObject effect = Instantiate(hitEffect, point, Quaternion.identity);
        
        Destroy(effect , 1f);
    }
}
