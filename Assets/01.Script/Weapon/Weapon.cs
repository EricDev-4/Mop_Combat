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

    [Header("Third Person")]
    public bool thirdPerson;
    [Min(0.01f)] public float reloadDuration = 2f;
    public bool IsReloading { get; private set; }
    private float reloadRemaining;
    private Transform ownerRoot;
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
      
        ownerRoot = GetComponentInParent<PhotonView>()?.transform ?? transform.root;
        if (!thirdPerson && adsPivot != null && weaponCam != null)
        {
            originAimPos = adsPivot.localPosition;
            originAimQuaternion = adsPivot.localRotation;
            originFov = weaponCam.fieldOfView;
        }

        if (adsFov <= 1f)
            adsFov = Mathf.Max(1f, originFov - 15f);
        RefreshAmmoUI();
    }
    
    private void Update()
    {
        if(nextFire > 0)
            nextFire -= Time.deltaTime;

        if (IsReloading)
        {
            reloadRemaining -= Time.deltaTime;
            if (reloadRemaining <= 0f)
            {
                mag--;
                ammo = magAmmo;
                IsReloading = false;
                RefreshAmmoUI();
            }
        }
        if (thirdPerson && Cursor.lockState != CursorLockMode.Locked)
        {
            isAiming = false;
            return;
        }
        if (Input.GetButton("Fire1")) TryFire();
        if (Input.GetKeyDown(KeyCode.R)) TryReload();
        if (thirdPerson) isAiming = Input.GetMouseButton(1);
        else if (Input.GetMouseButtonDown(1)) isAiming = !isAiming;
        if (!thirdPerson) UpdateADS();
    }

    public bool TryReload()
    {
        if (!enabled || IsReloading || mag <= 0 || ammo >= magAmmo) return false;
        IsReloading = true;
        reloadRemaining = Mathf.Max(0.01f, reloadDuration);
        if (!thirdPerson && _animator != null) _animator.SetTrigger(ReloadTrigger);
        return true;
    }

    public bool TryFire()
    {
        if (!enabled || IsReloading || nextFire > 0f || ammo <= 0 || camera == null) return false;
        nextFire = 1f / Mathf.Max(0.01f, fireRate);
        Fire();
        return true;
    }

    private void OnDisable()
    {
        IsReloading = false;
        isAiming = false;
    }

    private void RefreshAmmoUI()
    {
        if (magText != null) magText.text = mag.ToString();
        if (ammoText != null) ammoText.text = ammo + "/" + magAmmo;
    }

    private void UpdateADS()
    {
        if (adsPivot == null || adsTarget == null || _ADSPoint == null || weaponCam == null) return;
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

    private void Fire()
    {
        if (_recoil != null) _recoil.RecoilFire();
        ammo--;
        RefreshAmmoUI();
        if (_playerEffects != null) _playerEffects.SendMuzzleFlash();
        if (TryGetShotHit(out RaycastHit hit))
        {
            Health victim = hit.collider.GetComponentInParent<Health>();
            if (victim != null && victim.TryGetComponent<PhotonView>(out var victimView))
                victimView.RPC(nameof(Health.TakeDamage), RpcTarget.All, damage);
            if (_playerEffects != null) _playerEffects.SendHitEffect(hit.point);
        }
    }

    // Camera selects the aim point; the weapon can only reach it from its actual muzzle.
    public bool TryGetShotHit(out RaycastHit hit)
    {
        hit = default;
        if (camera == null) return false;
        if (ownerRoot == null) ownerRoot = GetComponentInParent<PhotonView>()?.transform ?? transform.root;
        Ray ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        bool cameraHit = CastIgnoringOwner(ray.origin, ray.direction, 100f, out hit);
        if (!thirdPerson || _FirePoint == null) return cameraHit;
        Vector3 target = cameraHit ? hit.point : ray.GetPoint(100f);
        Vector3 muzzle = _FirePoint.transform.position;
        Vector3 chest = ownerRoot.position + Vector3.up * 1.2f;
        // A muzzle poking through cover must not start its shot on the far side of the wall.
        Vector3 toMuzzle = muzzle - chest;
        if (CastIgnoringOwner(chest, toMuzzle.normalized, toMuzzle.magnitude, out hit)) return true;
        foreach (Collider overlap in Physics.OverlapSphere(muzzle, 0.025f,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            if (!IsOwner(overlap)) return false; // Embedded muzzle: consume shot, no damage beyond cover.
        if (Vector3.Dot(target - chest, ray.direction) <= 0f) target = muzzle + ray.direction * 100f;
        Vector3 direction = target - muzzle;
        return CastIgnoringOwner(muzzle, direction.normalized, direction.magnitude + 0.02f, out hit);
    }

    private bool IsOwner(Collider collider) => collider.transform == ownerRoot || collider.transform.IsChildOf(ownerRoot);

    private bool CastIgnoringOwner(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
    {
        nearest = default;
        float closest = float.PositiveInfinity;
        foreach (RaycastHit candidate in Physics.RaycastAll(origin, direction, distance,
                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            if (IsOwner(candidate.collider) || candidate.distance >= closest) continue;
            nearest = candidate;
            closest = candidate.distance;
        }
        return closest < float.PositiveInfinity;
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
