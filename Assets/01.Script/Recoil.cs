
using UnityEngine;

public class Recoil : MonoBehaviour
{
    [SerializeField] private Weapon player;
    
    //Bools
    private bool isAiming;
    
    // Rotation
    private Vector3 currentRotation;
    private Vector3 targetRotation;
    
    //Hipfire Recoil
    [SerializeField] private float recoilX;
    [SerializeField] private float recoilY;
    [SerializeField] private float recoilZ;
    
    //ADS Recoil
    [SerializeField] private float aimRecoilX;
    [SerializeField] private float aimRecoilY;
    [SerializeField] private float aimRecoilZ;

    
    //Settings
    [SerializeField] private float snappiness;
    [SerializeField] private float returnSpeed;

    private void Start()
    {
        player = GetComponentInChildren<Weapon>();
    }

    private void Update()
    {
        isAiming = player.isAiming;
        targetRotation = Vector3.Lerp(targetRotation, Vector3.zero, returnSpeed * Time.deltaTime);
        currentRotation = Vector3.Slerp(currentRotation, targetRotation, snappiness * Time.fixedDeltaTime);
        transform.localRotation = Quaternion.Euler(currentRotation);
    }

    public void RecoilFire()
    {
        if(isAiming) 
            targetRotation += new Vector3(aimRecoilX, Random.Range(-aimRecoilY , aimRecoilY),  Random.Range(-aimRecoilZ, aimRecoilZ) );
        else
            targetRotation += new Vector3(recoilX, Random.Range(-recoilY , recoilY),  Random.Range(-recoilZ, recoilZ) );

    }
}
