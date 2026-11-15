using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(AudioSource), typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public enum PlayerSlot
    {
        Player1, // WASD + Space
        Player2  // Strza³ki + Enter / M
    }

    [Header("Ustawienia Gracza")]
    [SerializeField] private PlayerSlot playerSlot = PlayerSlot.Player1;

    [Header("Referencje")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private Transform shotSpawnPoint;

    [Header("Parametry ruchu i broni")]
    [SerializeField] private float moveSpeed = 7.0f;
    [SerializeField] private float rotationSpeed = 200.0f; // stopnie na sekundê
    [SerializeField] private float stepRotationAngle = 2.0f; // krok dla metod zewnêtrznych
    [SerializeField] private float fireCooldown = 0.3f;

    [Header("Œmieræ i rozpad")]
    [SerializeField] private float explosionForce = 4.5f;
    [SerializeField] private float explosionRadius = 2.0f;
    [SerializeField] private float upwardModifier = 0.6f;
    [SerializeField] private float delayBeforeFade = 1.0f;
    [SerializeField] private float fadeDuration = 0.8f;

    [Header("DŸwiêki")]
    [SerializeField] private AudioClip shootSound;
    [SerializeField, Range(0f, 1f)] private float shootVolume = 0.5f;
    [SerializeField] private AudioClip deathSound;
    [SerializeField, Range(0f, 1f)] private float deathVolume = 0.8f;
    [SerializeField] private AudioClip[] footstepSounds = new AudioClip[5];
    [SerializeField] private float footstepInterval = 0.35f;
    [SerializeField, Range(0f, 1f)] private float footstepVolume = 0.8f;

    [Header("Sterowanie (New Input System)")]
    [Tooltip("Zostaw puste, aby u¿yæ domyœlnych klawiszy dla wybranego Player Slot")]
    [SerializeField] private InputAction moveAction;
    [SerializeField] private InputAction shootAction;

    [Header("Power-up: Podwójne naboje")]
    [SerializeField] private float doubleShotSpacing = 0.35f; // odstêp miêdzy nabojami obok siebie
    private float doubleShotEndTime;
    public bool IsDoubleShotActive => Time.time < doubleShotEndTime;

    private AudioSource audioSource;
    private CharacterController controller;
    private float targetYaw;
    private float nextFireTime;
    private float nextFootstepTime;
    private int lastFootstepIndex = -1;
    private bool isDead;

    public float TargetRotation => targetYaw;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        controller = GetComponent<CharacterController>();
        targetYaw = transform.localEulerAngles.y;

        SetupDefaultBindings();
    }

    private void OnEnable()
    {
        moveAction.Enable();
        shootAction.Enable();
    }

    private void OnDisable()
    {
        moveAction.Disable();
        shootAction.Disable();
    }

    private void Update()
    {
        if (Time.timeScale == 0f || isDead) return;

        Vector2 input = moveAction.ReadValue<Vector2>();

        HandleRotation(input.x);
        HandleMovement(input.y);
        HandleShooting();
    }

    private void HandleRotation(float turnInput)
    {
        if (Mathf.Abs(turnInput) > 0.01f)
        {
            targetYaw = Mathf.Repeat(targetYaw + turnInput * rotationSpeed * Time.deltaTime, 360f);
        }

        float currentYaw = transform.localEulerAngles.y;
        float smoothedYaw = Mathf.MoveTowardsAngle(currentYaw, targetYaw, rotationSpeed * Time.deltaTime);

        Vector3 euler = transform.localEulerAngles;
        euler.y = smoothedYaw;
        transform.localEulerAngles = euler;
    }

    private void HandleMovement(float forwardInput)
    {
        if (Mathf.Abs(forwardInput) > 0.01f)
        {
            Vector3 move = transform.forward * (forwardInput * moveSpeed * Time.deltaTime);
            controller.Move(move);
            HandleFootsteps();
        }
        else
        {
            nextFootstepTime = 0f;
        }
    }

    private void HandleFootsteps()
    {
        if (Time.time < nextFootstepTime || footstepSounds == null || footstepSounds.Length == 0)
            return;

        int index = GetRandomFootstepIndex();
        AudioClip clip = footstepSounds[index];

        if (clip != null)
        {
            audioSource.PlayOneShot(clip, footstepVolume);
            lastFootstepIndex = index;
        }

        nextFootstepTime = Time.time + footstepInterval;
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

    private void HandleShooting()
    {
        if (shootAction.IsPressed() && Time.time >= nextFireTime)
        {
            Fire();
            nextFireTime = Time.time + fireCooldown;
        }
    }

    private void Fire()
    {
        if (projectilePrefab == null || shotSpawnPoint == null)
        {
            Debug.LogError($"[{nameof(PlayerController)}] Brakuje przypisanych referencji w Inspektorze!", this);
            return;
        }

        if (IsDoubleShotActive)
        {
            Vector3 rightOffset = transform.right * (doubleShotSpacing * 0.5f);
            Instantiate(projectilePrefab, shotSpawnPoint.position - rightOffset, transform.rotation);
            Instantiate(projectilePrefab, shotSpawnPoint.position + rightOffset, transform.rotation);
        }
        else
        {
            Instantiate(projectilePrefab, shotSpawnPoint.position, transform.rotation);
        }

        if (shootSound != null)
        {
            audioSource.PlayOneShot(shootSound, shootVolume);
        }
        else if (audioSource.clip != null)
        {
            audioSource.PlayOneShot(audioSource.clip, shootVolume);
        }
    }

    /// <summary>
    /// Wywo³aj z PlayerHealth, gdy gracz straci wszystkie ¿ycia.
    /// </summary>
    public void Die()
    {
        if (isDead) return;
        isDead = true;

        if (deathSound != null)
        {
            AudioSource.PlayClipAtPoint(deathSound, transform.position, deathVolume);
        }

        SpawnDeathDebris();
        SetPlayerVisibleAndSolid(false);
    }

    /// <summary>
    /// Wywo³aj w PlayerHealth.ResetLives(), aby przywróciæ widocznoœæ i sterowanie gracza.
    /// </summary>
    public void Revive()
    {
        isDead = false;
        targetYaw = transform.localEulerAngles.y;
        SetPlayerVisibleAndSolid(true);
    }

    private void SetPlayerVisibleAndSolid(bool state)
    {
        foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
        {
            r.enabled = state;
        }

        if (controller != null)
        {
            controller.enabled = state;
        }
    }

    private void SpawnDeathDebris()
    {
        MeshRenderer[] originalParts = GetComponentsInChildren<MeshRenderer>();
        if (originalParts.Length == 0) return;

        GameObject debrisRoot = new GameObject($"{gameObject.name}_DeathDebris");
        debrisRoot.transform.position = transform.position;

        List<MeshRenderer> clonedRenderers = new List<MeshRenderer>();

        foreach (MeshRenderer origPart in originalParts)
        {
            if (!origPart.enabled) continue;

            MeshFilter origFilter = origPart.GetComponent<MeshFilter>();
            if (origFilter == null || origFilter.sharedMesh == null) continue;

            GameObject partClone = new GameObject(origPart.name);
            partClone.transform.SetParent(debrisRoot.transform);
            partClone.transform.SetPositionAndRotation(origPart.transform.position, origPart.transform.rotation);
            partClone.transform.localScale = origPart.transform.lossyScale;

            MeshFilter mf = partClone.AddComponent<MeshFilter>();
            mf.sharedMesh = origFilter.sharedMesh;

            MeshRenderer mr = partClone.AddComponent<MeshRenderer>();
            mr.sharedMaterials = origPart.sharedMaterials;
            clonedRenderers.Add(mr);

            partClone.AddComponent<BoxCollider>();

            Rigidbody rb = partClone.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            rb.AddExplosionForce(explosionForce, transform.position, explosionRadius, upwardModifier, ForceMode.Impulse);
        }

        PlayerDebrisFader fader = debrisRoot.AddComponent<PlayerDebrisFader>();
        fader.StartFade(clonedRenderers, delayBeforeFade, fadeDuration);
    }

    public void RotateClockwise()
    {
        targetYaw = Mathf.Repeat(targetYaw + stepRotationAngle, 360f);
    }

    public void RotateCounterClockwise()
    {
        targetYaw = Mathf.Repeat(targetYaw - stepRotationAngle, 360f);
    }

    private void SetupDefaultBindings()
    {
        if (playerSlot == PlayerSlot.Player1)
        {
            if (moveAction.bindings.Count == 0)
            {
                moveAction.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/w")
                    .With("Down", "<Keyboard>/s")
                    .With("Left", "<Keyboard>/a")
                    .With("Right", "<Keyboard>/d");
            }

            if (shootAction.bindings.Count == 0)
            {
                shootAction.AddBinding("<Keyboard>/space");
            }
        }
        else // Player2
        {
            if (moveAction.bindings.Count == 0)
            {
                moveAction.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/upArrow")
                    .With("Down", "<Keyboard>/downArrow")
                    .With("Left", "<Keyboard>/leftArrow")
                    .With("Right", "<Keyboard>/rightArrow");
            }

            if (shootAction.bindings.Count == 0)
            {
                shootAction.AddBinding("<Keyboard>/enter");
                shootAction.AddBinding("<Keyboard>/m");
            }
        }
    }

    public void ActivateDoubleShot(float duration)
    {
        doubleShotEndTime = Time.time + duration;
    }

    private class PlayerDebrisFader : MonoBehaviour
    {
        public void StartFade(List<MeshRenderer> renderers, float delay, float duration)
        {
            StartCoroutine(FadeRoutine(renderers, delay, duration));
        }

        private IEnumerator FadeRoutine(List<MeshRenderer> renderers, float delay, float duration)
        {
            List<Material> materials = new List<Material>();

            foreach (MeshRenderer r in renderers)
            {
                foreach (Material mat in r.materials)
                {
                    SetMaterialTransparent(mat);
                    materials.Add(mat);
                }
            }

            yield return new WaitForSeconds(delay);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float alpha = Mathf.Clamp01(1f - (elapsed / duration));

                foreach (Material mat in materials)
                {
                    if (mat == null) continue;

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
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 2f);
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
}