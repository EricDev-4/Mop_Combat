using UnityEngine;
using Photon.Pun;
using TMPro;

[DefaultExecutionOrder(-100)]
public class PlayerSetup : MonoBehaviourPun
{
    [SerializeField] private Movement movement;
    [SerializeField] private GameObject playerCamera;
    [SerializeField] private GameObject thirdPersonVisual;
    public NetworkThirdPersonController thirdPersonController;
    public GameObject personalHud;

    public string nickname;
    public TextMeshPro nicknameText;
    public GameObject nameTagHolder;
    private void Start()
    {
        ApplyPresentation(PhotonNetwork.InRoom && photonView.IsMine);
    }

    private void ApplyPresentation(bool isLocalPlayer)
    {
        if (thirdPersonController != null)
        {
            thirdPersonVisual.SetActive(true);
            thirdPersonController.ConfigureOwnership(isLocalPlayer);
            playerCamera.SetActive(isLocalPlayer);
            foreach (Camera view in GetComponentsInChildren<Camera>(true))
                view.enabled = isLocalPlayer;
            foreach (AudioListener listener in GetComponentsInChildren<AudioListener>(true))
                listener.enabled = isLocalPlayer;
            foreach (ThirdPersonOrbitCamera orbit in GetComponentsInChildren<ThirdPersonOrbitCamera>(true))
                orbit.enabled = isLocalPlayer;
            foreach (Weapon weapon in GetComponentsInChildren<Weapon>(true))
                weapon.enabled = isLocalPlayer;
            if (personalHud != null) personalHud.SetActive(isLocalPlayer);
            if (nameTagHolder != null) nameTagHolder.SetActive(!isLocalPlayer);
            Health health = GetComponent<Health>();
            if (health != null) health.isLocalPlayer = isLocalPlayer;
            return;
        }
        if (playerCamera == null || thirdPersonVisual == null)
            Debug.LogError("PlayerSetup requires both first-person and third-person roots.", this);

        if (thirdPersonVisual != null)
            thirdPersonVisual.SetActive(!isLocalPlayer);

        if (movement != null)
        {
            movement.enabled = isLocalPlayer;
            if (isLocalPlayer && playerCamera != null)
                movement.ConfigureHeadBob(playerCamera.transform);
        }

        // The remote body is outside CameraRoot. Disable first-person input,
        // cameras, arms and personal UI together on remote players.
        if (playerCamera != null)
        {
            playerCamera.SetActive(isLocalPlayer);

            foreach (Camera cameraComponent in playerCamera.GetComponentsInChildren<Camera>(true))
                cameraComponent.enabled = isLocalPlayer;

            foreach (AudioListener listener in playerCamera.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = isLocalPlayer;

            MouseLook mouseLook = playerCamera.GetComponent<MouseLook>();
            if (mouseLook != null)
                mouseLook.enabled = isLocalPlayer;

            foreach (Weapon weapon in playerCamera.GetComponentsInChildren<Weapon>(true))
                weapon.enabled = isLocalPlayer;

            // One equipped weapon; the prefab has no switchable weapon list.
            foreach (WeaponSwitcher switcher in playerCamera.GetComponentsInChildren<WeaponSwitcher>(true))
                switcher.enabled = false;

            foreach (WeaponSway sway in playerCamera.GetComponentsInChildren<WeaponSway>(true))
                sway.enabled = isLocalPlayer;
        }
        
        if (nameTagHolder != null)
            nameTagHolder.SetActive(!isLocalPlayer);
    }
    
    [PunRPC]
    public void SetNickname(string _name)
    {
        nickname = _name;

        if (nicknameText != null)
            nicknameText.text = nickname;
    }
}
