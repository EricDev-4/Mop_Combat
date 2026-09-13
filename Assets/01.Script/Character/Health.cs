using Photon.Pun;
using TMPro;
using UnityEngine;

public class Health : MonoBehaviour
{
    public int health;
    public bool isLocalPlayer;
    public TextMeshProUGUI healthText;
    private bool dead;

    private void Start() { RefreshUI(); }
    private void RefreshUI()
    {
        if (healthText != null) healthText.text = health.ToString();
    }

    [PunRPC]
    public void TakeDamage(int damage)
    {
        if (dead || damage <= 0) return;
        health = Mathf.Max(0, health - damage);
        RefreshUI();
        if (health > 0) return;
        dead = true;
        PhotonView view = GetComponent<PhotonView>();
        if (view == null || !view.IsMine) return;
        if (RoomManager.instance != null) RoomManager.instance.RespawnPlayer(gameObject);
        else PhotonNetwork.Destroy(gameObject);
    }
}
