using System.Collections;
using UnityEngine;
using Photon.Pun;
using Random = UnityEngine.Random;

public class RoomManager : MonoBehaviourPunCallbacks
{
    public static RoomManager instance;

    private void Awake()
    {
        instance = this;
    }

    
    public GameObject playerPb;
    public Transform[] spawnPoint;

    public GameObject roomCam;

    public GameObject nameUI;
    public GameObject connectingUI;
    
    private string nickname = "Unknowen";
    [SerializeField] private string roomName = "test";

    public void ChangeNickname(string _name)
    {
        nickname = _name;
    }

    public void JoinroomButtonPressed()
    {
        Debug.Log("Connecting");
        //Photon 서버 연결을 비동기로 시작하는 메서드
        PhotonNetwork.ConnectUsingSettings();
        
        nameUI.SetActive(false);
        connectingUI.SetActive(true);
    }
    
    private void Start()
    {

    }

    public override void OnConnectedToMaster()
    {
        base.OnConnectedToMaster();
        
        Debug.Log("Connected to Server");

        PhotonNetwork.JoinLobby();
    }

    public override void OnJoinedLobby()
    {
        base.OnJoinedLobby();

        PhotonNetwork.JoinOrCreateRoom(roomName, null, null);
        
        Debug.Log("We're connected and in a room now");
    }

    public override void OnJoinedRoom()
    {
        base.OnJoinedRoom();
        Debug.Log("Joined Room");

        GameObject spawnedPlayer = SpawnPlayer();
        if (spawnedPlayer == null)
            return;

        if (connectingUI != null)
            connectingUI.SetActive(false);

        if (nameUI != null)
            nameUI.SetActive(false);

        StartCoroutine(SwitchToPlayerCamera(spawnedPlayer));
    }

    private IEnumerator SwitchToPlayerCamera(GameObject spawnedPlayer)
    {
        // PlayerSetup.Start가 로컬 플레이어 카메라를 켤 때까지 한 프레임 기다린다.
        yield return null;

        if (spawnedPlayer == null) yield break;
        Camera playerView = spawnedPlayer.GetComponentInChildren<Camera>();
        if (playerView == null || !playerView.gameObject.activeInHierarchy)
        {
            Debug.LogError("로컬 플레이어 카메라가 활성화되지 않아 Room Camera를 유지합니다.", spawnedPlayer);
            yield break;
        }

        if (roomCam != null)
            roomCam.SetActive(false);
    }

    private GameObject localPlayer;

    public void RespawnPlayer(GameObject previousPlayer)
    {
        if (previousPlayer == null || !previousPlayer.TryGetComponent<PhotonView>(out var view) || !view.IsMine)
            return;
        previousPlayer.SetActive(false);
        PhotonNetwork.Destroy(previousPlayer);
        localPlayer = null;
        GameObject replacement = SpawnPlayer();
        if (replacement != null) StartCoroutine(SwitchToPlayerCamera(replacement));
    }

    public override void OnLeftRoom()
    {
        localPlayer = null;
        if (roomCam != null) roomCam.SetActive(true);
        if (nameUI != null) nameUI.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public GameObject SpawnPlayer()
    {
        if (!PhotonNetwork.InRoom) return null;
        if (localPlayer != null) return localPlayer;
        if (playerPb == null || spawnPoint == null || spawnPoint.Length == 0)
        {
            Debug.LogError("Player Prefab 또는 Spawn Point가 설정되지 않았습니다.", this);
            return null;
        }

        int spawnNum = Random.Range(0, spawnPoint.Length);
        GameObject spawnedPlayer = PhotonNetwork.Instantiate(
            playerPb.name,
            spawnPoint[spawnNum].position,
            spawnPoint[spawnNum].rotation);

        localPlayer = spawnedPlayer;
        Health health = spawnedPlayer.GetComponent<Health>();
        if (health != null)
            health.isLocalPlayer = true;

        PhotonView spawnedView = spawnedPlayer.GetComponent<PhotonView>();
        if (spawnedView != null)
            spawnedView.RPC("SetNickname", RpcTarget.AllBuffered, nickname);

        return spawnedPlayer;
    }
}
