using UnityEngine;

[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(AudioLowPassFilter))]
public class BciAudioMuffle : MonoBehaviour
{
    [Header("Zakres t³umienia (Hz)")]
    [Tooltip("Czêstotliwoœæ odciêcia przy zerowej synchronizacji (im mniej, tym bardziej za œcian¹).")]
    [SerializeField] private float muffledCutoffHz = 350f;
    [Tooltip("Czêstotliwoœæ odciêcia przy pe³nej synchronizacji (czysty dŸwiêk).")]
    [SerializeField] private float clearCutoffHz = 22000f;

    [Header("Dodatkowe œciszanie")]
    [Tooltip("G³oœnoœæ przy braku synchronizacji (0..1).")]
    [Range(0f, 1f)][SerializeField] private float muffledVolume = 0.45f;
    [Tooltip("G³oœnoœæ przy pe³nej synchronizacji (0..1).")]
    [Range(0f, 1f)][SerializeField] private float clearVolume = 1.0f;

    [Header("Wyg³adzanie przejœcia")]
    [SerializeField] private float smoothSpeed = 4f;

    private AudioSource _audioSource;
    private AudioLowPassFilter _lowPass;
    private float _currentSync;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        _lowPass = GetComponent<AudioLowPassFilter>();
    }

    private void Update()
    {
        float targetSync = 0f;

        if (BciParameters.Instance != null && BciParameters.Instance.hasSignal)
        {
            targetSync = BciParameters.Instance.combined;
        }

        _currentSync = Mathf.Lerp(_currentSync, targetSync, Time.deltaTime * smoothSpeed);

        // Wyk³adnicze przejœcie czêstotliwoœci (brzmi naturalnie dla ucha)
        float logMin = Mathf.Log(muffledCutoffHz);
        float logMax = Mathf.Log(clearCutoffHz);
        _lowPass.cutoffFrequency = Mathf.Exp(Mathf.Lerp(logMin, logMax, _currentSync));

        // P³ynne dopasowanie g³oœnoœci
        _audioSource.volume = Mathf.Lerp(muffledVolume, clearVolume, _currentSync);
    }
}