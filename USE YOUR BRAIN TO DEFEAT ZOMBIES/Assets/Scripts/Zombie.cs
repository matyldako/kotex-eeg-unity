using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(AudioSource))]

public class Zombie : MonoBehaviour
{
    

    [Header("Movement")]
    [Tooltip("Movement speed applied to the NavMeshAgent")]
    [SerializeField] private float moveSpeed = 3.5f;

    [Header("Player Targeting")]
    [Tooltip("Tag assigned to player objects")]
    [SerializeField] private string playerTag = "Player";
    [Tooltip("How often (in seconds) the zombie searches for the closest player")]
    [SerializeField] private float targetUpdateInterval = 0.2f;

    [Tooltip("Currently targeted closest player (Inspector preview)")]
    public Transform closestPlayer;

    [Header("Player Attack")]
    [Tooltip("Distance from which the zombie deals damage to the player")]
    [SerializeField] private float attackRange = 1.5f;
    [SerializeField] private int damage = 1;

    [Header("Hit Settings")]
    [SerializeField] private string bulletLayerName = "Bullet";
    [SerializeField] private bool destroyBulletOnHit = true;

    [Header("Death & Dismemberment")]
    [SerializeField] private float explosionForce = 4.5f;
    [SerializeField] private float explosionRadius = 2.0f;
    [SerializeField] private float upwardModifier = 0.6f;
    [SerializeField] private float delayBeforeFade = 1.0f;
    [SerializeField] private float fadeDuration = 0.8f;

    [Header("Audio")]
    [SerializeField] private AudioClip hitSound;
    [SerializeField, Range(0f, 1f)] private float hitVolume = 0.8f;
    [SerializeField] private AudioClip[] footstepSounds = new AudioClip[5];
    [SerializeField] private float footstepInterval = 0.45f;
    [SerializeField, Range(0f, 1f)] private float footstepVolume = 0.25f;
    [SerializeField] private float globalFootstepCooldown = 0.18f;

    private NavMeshAgent agent;
    private AudioSource audioSource;
    private Collider mainCollider;
    private int bulletLayer;
    private float updateTimer;
    private float nextFootstepTime;
    private int lastFootstepIndex = -1;
    private bool isDead;
    private static float freezeEndTime = 0f;
    public static bool AreZombiesFrozen => Time.time < freezeEndTime;
    public static float GlobalSpeedMultiplier { get; set; } = 1.0f;

    private static float nextGlobalFootstepTime;
    public static event System.Action OnZombieDied;

    public float MoveSpeed
    {
        get => moveSpeed;
        set
        {
            moveSpeed = Mathf.Max(0f, value);
            if (agent != null)
            {
                agent.speed = moveSpeed;
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init()
    {
        freezeEndTime = 0f;
        GlobalSpeedMultiplier = 1.0f;
        nextGlobalFootstepTime = 0f;
    }

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        audioSource = GetComponent<AudioSource>();
        mainCollider = GetComponent<Collider>();
        bulletLayer = LayerMask.NameToLayer(bulletLayerName);

        agent.speed = moveSpeed;

        if (bulletLayer == -1)
        {
            Debug.LogWarning($"[{nameof(Zombie)}] Layer '{bulletLayerName}' does not exist in Project Settings!", this);
        }
    }

    private void OnValidate()
    {
        if (agent == null)
        {
            agent = GetComponent<NavMeshAgent>();
        }

        if (agent != null)
        {
            agent.speed = Mathf.Max(0f, moveSpeed);
        }
    }

    private void Start()
    {
        FindClosestPlayer();
        nextFootstepTime = Time.time + Random.Range(0f, footstepInterval);
    }

    private void Update()
    {
        if (isDead) return;

        if (AreZombiesFrozen)
        {
            if (agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
            }
            return;
        }

        if (agent.isOnNavMesh)
        {
            agent.isStopped = false;
            agent.speed = moveSpeed * GlobalSpeedMultiplier;
        }

        updateTimer += Time.deltaTime;
        if (updateTimer >= targetUpdateInterval)
        {
            updateTimer = 0f;
            FindClosestPlayer();
        }

        if (closestPlayer != null)
        {
            if (agent.isOnNavMesh)
            {
                agent.SetDestination(closestPlayer.position);
            }

            TryAttackClosestPlayer();
        }

        HandleFootsteps();
    }

    private void HandleFootsteps()
    {
        if (Time.timeScale == 0f || footstepSounds == null || footstepSounds.Length == 0)
            return;

        if (agent.isOnNavMesh && agent.velocity.sqrMagnitude > 0.1f)
        {
            if (Time.time >= nextFootstepTime && Time.time >= nextGlobalFootstepTime)
            {
                int index = GetRandomFootstepIndex();
                AudioClip clip = footstepSounds[index];

                if (clip != null)
                {
                    audioSource.pitch = Random.Range(0.9f, 1.1f);
                    audioSource.PlayOneShot(clip, footstepVolume);
                    lastFootstepIndex = index;

                    nextGlobalFootstepTime = Time.time + globalFootstepCooldown;
                }

                nextFootstepTime = Time.time + footstepInterval;
            }
        }
    }

    private int GetRandomFootstepIndex()
    {
        if (footstepSounds.Length == 1) return 0;

        int index;
        do
        {
            index = Random.Range(0, footstepSounds.Length);
        } while (index == lastFootstepIndex);

        return index;
    }

    private void TryAttackClosestPlayer()
    {
        float sqrDistance = (closestPlayer.position - transform.position).sqrMagnitude;
        if (sqrDistance <= attackRange * attackRange)
        {
            PlayerHealth playerHealth = closestPlayer.GetComponent<PlayerHealth>();
            if (playerHealth != null)
            {
                playerHealth.TakeDamage(damage);
            }
        }
    }

    private void FindClosestPlayer()
    {
        GameObject[] players = GameObject.FindGameObjectsWithTag(playerTag);

        Transform bestTarget = null;
        float closestDistanceSqr = Mathf.Infinity;
        Vector3 currentPosition = transform.position;

        foreach (GameObject player in players)
        {
            if (!player.activeInHierarchy) continue;

            float dSqrToTarget = (player.transform.position - currentPosition).sqrMagnitude;
            if (dSqrToTarget < closestDistanceSqr)
            {
                closestDistanceSqr = dSqrToTarget;
                bestTarget = player.transform;
            }
        }

        closestPlayer = bestTarget;
    }

    public static void FreezeAllZombies(float duration)
    {
        freezeEndTime = Mathf.Max(freezeEndTime, Time.time + duration);
    }

    private void OnTriggerEnter(Collider other)
    {
        CheckBulletHit(other.gameObject);
    }

    private void OnCollisionEnter(Collision collision)
    {
        CheckBulletHit(collision.gameObject);
    }

    private void CheckBulletHit(GameObject hitObject)
    {
        if (isDead) return;

        if (hitObject.layer == bulletLayer)
        {
            if (destroyBulletOnHit)
            {
                Destroy(hitObject);
            }

            if (hitSound != null)
            {
                AudioSource.PlayClipAtPoint(hitSound, transform.position, hitVolume);
            }

            Die(hitObject.transform.position);
        }
    }

    private void Die(Vector3 impactPoint)
    {
        isDead = true;
        OnZombieDied?.Invoke();

        // Wy³¹cz nawigacjê i g³ówny kolider, aby zw³oki nie blokowa³y graczy ani pocisków
        if (agent != null) agent.enabled = false;
        if (mainCollider != null) mainCollider.enabled = false;

        // Jeœli zombie ma Animator, wy³¹cz go, aby nie trzyma³ koœci/czêœci w miejscu
        Animator animator = GetComponentInChildren<Animator>();
        if (animator != null) animator.enabled = false;

        MeshRenderer[] bodyParts = GetComponentsInChildren<MeshRenderer>();
        Vector3 explosionOrigin = Vector3.Lerp(transform.position, impactPoint, 0.5f);

        foreach (MeshRenderer part in bodyParts)
        {
            GameObject partObj = part.gameObject;

            // Dodaj kolider dopasowany do klockowatej czêœci cia³a
            if (partObj.GetComponent<Collider>() == null)
            {
                partObj.AddComponent<BoxCollider>();
            }

            // Dodaj fizykê i rozrzuæ czêœci
            Rigidbody rb = partObj.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = partObj.AddComponent<Rigidbody>();
            }

            rb.mass = 0.5f;
            rb.AddExplosionForce(explosionForce, explosionOrigin, explosionRadius, upwardModifier, ForceMode.Impulse);
        }

        StartCoroutine(FadeAndDestroy(bodyParts));
    }

    private IEnumerator FadeAndDestroy(MeshRenderer[] renderers)
    {
        List<Material> materials = new List<Material>();

        foreach (MeshRenderer r in renderers)
        {
            // .materials tworzy unikalne instancje dla tego konkretnego zombie
            foreach (Material mat in r.materials)
            {
                SetMaterialTransparent(mat);
                materials.Add(mat);
            }
        }

        // Odczekaj 1 sekundê (domyœlnie) zanim zacznie siê rozp³ywaæ
        yield return new WaitForSeconds(delayBeforeFade);

        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            float alpha = Mathf.Clamp01(1f - (elapsed / fadeDuration));

            foreach (Material mat in materials)
            {
                if (mat == null) continue;

                // Obs³uga zarówno URP (_BaseColor), jak i Built-in Standard (_Color)
                if (mat.HasProperty("_BaseColor"))
                {
                    Color c = mat.GetColor("_BaseColor");
                    c.a = alpha;
                    mat.SetColor("_BaseColor", c);
                }
                else if (mat.HasProperty("_Color"))
                {
                    Color c = mat.color;
                    c.a = alpha;
                    mat.color = c;
                }
            }

            yield return null;
        }

        Destroy(gameObject);
    }

    private void SetMaterialTransparent(Material mat)
    {
        // Konfiguracja dla Universal Render Pipeline (URP Lit / Simple Lit)
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f); // 1 = Transparent
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        // Konfiguracja dla Built-in Standard Shader
        else if (mat.HasProperty("_Mode"))
        {
            mat.SetFloat("_Mode", 2f); // 2 = Fade
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
    }
}