using System;
using UnityEngine;

public class PlayerHealth : MonoBehaviour
{
    [Header("Ustawienia ¿ycia")]
    [SerializeField] private int maxLives = 3;
    [Tooltip("Czas nietykalnoœci (w sekundach) po otrzymaniu obra¿eñ")]
    [SerializeField] private float damageCooldown = 1.5f;

    private int currentLives;
    private float lastDamageTime = -Mathf.Infinity;
    private PlayerController playerController;

    public int CurrentLives => currentLives;
    public bool IsInvulnerable => Time.time < lastDamageTime + damageCooldown;

    public event Action<int> OnLivesChanged;

    private void Awake()
    {
        currentLives = maxLives;
        playerController = GetComponent<PlayerController>();
    }

    private void Start()
    {
        OnLivesChanged?.Invoke(currentLives);
    }

    public void TakeDamage(int damage = 1)
    {
        // Ignoruj obra¿enia, jeœli gracz nie ¿yje lub trwa cooldown
        if (currentLives <= 0 || IsInvulnerable) return;

        lastDamageTime = Time.time;
        currentLives = Mathf.Max(0, currentLives - damage);
        OnLivesChanged?.Invoke(currentLives);

        if (currentLives == 0)
        {
            if (playerController != null)
            {
                playerController.Die();
            }

            // Wy³¹czenie gracza (zombie automatycznie prze³¹cz¹ siê na drugiego gracza)
            gameObject.SetActive(false);
        }
    }

    public void ResetLives()
    {
        currentLives = maxLives;
        lastDamageTime = -Mathf.Infinity;
        gameObject.SetActive(true);

        if (playerController != null)
        {
            playerController.Revive();
        }

        OnLivesChanged?.Invoke(currentLives);
    }

    public void Heal(int amount = 1)
    {
        if (currentLives <= 0) return;

        currentLives = Mathf.Min(maxLives, currentLives + amount);
        OnLivesChanged?.Invoke(currentLives);
    }
}