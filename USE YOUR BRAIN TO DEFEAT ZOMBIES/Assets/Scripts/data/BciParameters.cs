using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(SyncReceiver))]
public class BciParameters : MonoBehaviour
{
    public static BciParameters Instance { get; private set; }

    [Header("Tryb testowy (bez Pythona / czepków)")]
    [Tooltip("Zaznacz, aby sterowaæ wartoœciami rêcznie z suwaków poni¿ej.")]
    public bool simulateValues = false;

    [Header("Parametry na ¿ywo (0..1)")]
    [Range(0f, 1f)] public float combined;
    [Range(0f, 1f)] public float score;
    [Range(0f, 1f)] public float theta;
    [Range(0f, 1f)] public float alpha;
    [Range(0f, 1f)] public float gamma;

    [Header("Status po³¹czenia")]
    public bool hasSignal;
    public bool isCalibrating;
    [Range(0f, 1f)] public float calibrationProgress;

    [Header("Zdarzenia dla grafików / Level Designerów")]
    public UnityEvent<float> onCombinedUpdated;
    public UnityEvent<float> onAlphaUpdated;
    public UnityEvent<float> onBetaUpdated;

    private SyncReceiver _receiver;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _receiver = GetComponent<SyncReceiver>();
    }

    private void Update()
    {
        if (simulateValues)
        {
            hasSignal = true;
            isCalibrating = false;
            calibrationProgress = 1f;
            // W trybie symulacji Combined automatycznie liczy œredni¹ z suwaków, jeœli nie ruszysz go rêcznie
        }
        else
        {
            hasSignal = _receiver.HasSignal;
            isCalibrating = _receiver.IsCalibrating;
            calibrationProgress = _receiver.CalibrationProgress;

            combined = _receiver.Combined;
            score = _receiver.Score;
            theta = _receiver.Get("theta");
            alpha = _receiver.Get("alpha");
            gamma = _receiver.Get("gamma");
        }

        if (hasSignal)
        {
            onCombinedUpdated?.Invoke(combined);
            onAlphaUpdated?.Invoke(alpha);
        }
    }
}