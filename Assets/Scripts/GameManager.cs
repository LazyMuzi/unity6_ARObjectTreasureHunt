using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance;

    public CameraManager cameraManager;
    public ModelManager modelManager;
    public Text detectionResultText;

    void Awake()
    {
        if(Instance == null)
        {
            Instance = this;
        }
        else if(Instance != this)
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        modelManager.OnDetectionResult.AddListener(OnDetectionResult);
    }

    public void InitializeAndPlayCamera()
    {
        cameraManager.InitializeAndPlayCamera();
    }

    public void TakePictureAndProcess()
    {
        cameraManager.TakePictureAndProcess();
    }

    private void OnDetectionResult(string label)
    {
        detectionResultText.text = label;
    }
}
