using System.Collections;
using UnityEngine;

public class WeaponSwitcher : MonoBehaviour
{

    
    [SerializeField] private GameObject[] weaponsGroup;

    private int selectedWeapon = 0;
    private int previousSelectedWeapon;
    private Weapon _weapon;
    private Coroutine swapCoroutine;

    private static readonly int SwapTrigger = Animator.StringToHash("Swap");
    private void Start()
    {
        for (int i = 0; i < weaponsGroup.Length; i++)
        {
            weaponsGroup[i].SetActive(false);
        }
        weaponsGroup[0].SetActive(true);
        _weapon = GetComponent<Weapon>();
        previousSelectedWeapon = selectedWeapon;
    }
    
    private void Update()
    {
        previousSelectedWeapon = selectedWeapon;
        if (Input.GetKeyDown(KeyCode.Alpha1))
            selectedWeapon = 0;

        if (Input.GetKeyDown(KeyCode.Alpha2))
            selectedWeapon = 1;
        
        if (Input.GetKeyDown(KeyCode.Alpha3))
            selectedWeapon = 2;
        
        if(previousSelectedWeapon != selectedWeapon)
            SelectWeapon();
            
    }

    void SelectWeapon()
    {
        if (swapCoroutine != null)
            StopCoroutine(swapCoroutine);

        swapCoroutine = StartCoroutine(SwapWeaponCoroutine(selectedWeapon));
    }

    private IEnumerator SwapWeaponCoroutine(int targetWeapon)
    {
        if (_weapon != null && _weapon._animator != null)
            _weapon._animator.SetTrigger(SwapTrigger);

        yield return new WaitForSeconds(0.5f);

        for (int i = 0; i < weaponsGroup.Length; i++)
            weaponsGroup[i].SetActive(i == targetWeapon);

        swapCoroutine = null;
    }
}
