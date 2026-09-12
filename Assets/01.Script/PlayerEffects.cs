using UnityEngine;
using Photon.Pun;

public class PlayerEffects : MonoBehaviourPun
{
    [SerializeField] private Transform firstPersonMuzzle;
    [SerializeField] private Transform thirdPersonMuzzle;
    [SerializeField] private GameObject muzzleFlashPrefab;
    [SerializeField] private GameObject hitEffectPrefab;

    private void Start()
    {
        if (firstPersonMuzzle == null || thirdPersonMuzzle == null ||
            muzzleFlashPrefab == null || hitEffectPrefab == null)
            Debug.LogError("PlayerEffects requires both muzzle points and effect prefabs.", this);
    }

    public void SendMuzzleFlash()
    {
        if (!photonView.IsMine)
            return;
        
        photonView.RPC(nameof(RPC_MuzzleFlash), RpcTarget.All);
    }

    public void SendHitEffect(Vector3 point)
    {
        if(!photonView.IsMine)
            return;
        
        photonView.RPC(nameof(RPC_HitEffect),  RpcTarget.All, point);
    }

    [PunRPC]
    private void RPC_MuzzleFlash()
    {
        Transform muzzle = photonView.IsMine ? firstPersonMuzzle : thirdPersonMuzzle;
        if (muzzle == null || muzzleFlashPrefab == null)
            return;

        GameObject effect = Instantiate(muzzleFlashPrefab, muzzle.position, muzzle.rotation, muzzle);
        foreach (Transform child in effect.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = muzzle.gameObject.layer;

        foreach (ParticleSystem particle in effect.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = particle.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }
        Destroy(effect, 1f);
    }

    [PunRPC]
    private void RPC_HitEffect(Vector3 point)
    {
        if (hitEffectPrefab == null)
            return;

        GameObject effect = Instantiate(hitEffectPrefab, point, Quaternion.identity);
        foreach (Transform child in effect.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = 0;
        Destroy(effect, 1f);
    }
}
