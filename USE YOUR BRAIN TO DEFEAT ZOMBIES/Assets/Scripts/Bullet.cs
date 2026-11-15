using UnityEngine;

public class Bullet : MonoBehaviour
{
    [SerializeField] private float speed = 15.0f;
    [SerializeField] private float lifeTime = 3.5f;

    private void Start()
    {
        Destroy(gameObject, lifeTime);
    }

    private void Update()
    {
        transform.Translate(Vector3.forward * Time.deltaTime * speed);
    }
}