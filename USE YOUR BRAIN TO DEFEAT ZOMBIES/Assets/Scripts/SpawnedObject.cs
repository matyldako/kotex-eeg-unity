using UnityEngine;

public abstract class SpawnedObject : MonoBehaviour
{
    [Header("Ustawienia obiektu")]
    [SerializeField] private float lifeTime = 10.0f;
    [SerializeField] private float rotationSpeed = 60.0f;
    [SerializeField] private string playerTag = "Player";

    [Header("Dźwięk podniesienia (opcjonalnie)")]
    [SerializeField] private AudioClip pickupSound;
    [SerializeField, Range(0f, 1f)] private float pickupVolume = 0.8f;

    private float timer;
    private bool isCollected;

    // Wywoływane automatycznie w edytorze przy dodaniu skryptu do obiektu
    private void Reset()
    {
        EnsureTriggerCollider();
    }

    // Zabezpieczenie przy uruchomieniu gry, gdyby kolider został usunięty później
    protected virtual void Awake()
    {
        EnsureTriggerCollider();
    }

    private void EnsureTriggerCollider()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.useGravity = false;

        Collider col = GetComponentInChildren<Collider>();
        if (col == null)
        {
            BoxCollider box = gameObject.AddComponent<BoxCollider>();
            float maxScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            if (maxScale > 0f && maxScale < 0.5f)
            {
                box.size = Vector3.one * (1.2f / maxScale);
            }
            col = box;
        }

        col.isTrigger = true;
    }

    protected virtual void Update()
    {
        // Powolne obracanie wokół osi Y
        transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.World);

        timer += Time.deltaTime;
        if (timer >= lifeTime)
        {
            Destroy(gameObject);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isCollected) return;

        PlayerController playerController = other.GetComponentInParent<PlayerController>();
        if (playerController == null) return;

        if (!playerController.CompareTag(playerTag) && !other.CompareTag(playerTag)) return;

        isCollected = true;
        OnPickup(playerController.gameObject);

        if (pickupSound != null)
        {
            AudioSource.PlayClipAtPoint(pickupSound, transform.position, pickupVolume);
        }

        Destroy(gameObject);
    }

    protected abstract void OnPickup(GameObject player);
}