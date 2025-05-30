using UnityEngine;
using UnityEngine.UI;

public class CameraManager : MonoBehaviour
{
    public ModelManager modelManager; // Inspector에서 할당
    private WebCamTexture webCamTexture;

    public RawImage rawImage;
    public RawImage photoImage;
    private bool isCameraInitialized = false;

    public void InitializeAndPlayCamera()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("카메라를 찾을 수 없습니다.");
            isCameraInitialized = false;
            return;
        }

        WebCamDevice device = WebCamTexture.devices[0]; // 기본 카메라 사용
        webCamTexture = new WebCamTexture(device.name);
        webCamTexture.Play();
        isCameraInitialized = true;
        Debug.Log("카메라가 초기화됨.");
    }

    void Update()
    {
        if(webCamTexture != null && webCamTexture.isPlaying)
        {
            rawImage.texture = webCamTexture;
        }
    }

    public void TakePictureAndProcess()
    {
        if (!isCameraInitialized)
        {
            Debug.LogError("카메라가 초기화되지 않았습니다. 사진을 촬영할 수 없습니다.");
            return;
        }

        // 1. 카메라로 사진을 찍는 로직 (예: WebCamTexture, Unity의 Camera.Render(), Native Camera Plugin 등)
        Texture2D photo = TakePhoto(); // 이 함수는 실제 사진 촬영 로직을 구현해야 함

        if (photo != null && modelManager != null)
        {
            modelManager.ExecuteDetection(photo);
        }
        else if (photo == null)
        {
            Debug.LogError("사진 촬영에 실패했거나 이미지가 없습니다.");
        }
        else
        {
            Debug.LogError("ModelManager가 할당되지 않았습니다.");
        }
    }

    private Texture2D TakePhoto()
    {
        if (!isCameraInitialized || webCamTexture == null || !webCamTexture.isPlaying)
        {
            Debug.LogError("카메라가 준비되지 않았거나 재생 중이 아닙니다.");
            return null;
        }

        // WebCamTexture의 현재 프레임을 Texture2D로 복사합니다.
        // WebCamTexture의 너비와 높이가 0보다 클 때만 Texture2D를 생성합니다.
        if (webCamTexture.width <= 0 || webCamTexture.height <= 0)
        {
            Debug.LogError("WebCamTexture의 너비 또는 높이가 유효하지 않습니다.");
            return null;
        }

        Texture2D photo = new Texture2D(webCamTexture.width, webCamTexture.height);
        photo.SetPixels(webCamTexture.GetPixels());
        photo.Apply();
        photoImage.texture = photo;

        Debug.Log("사진 촬영 완료!");
        return photo;
    }

    void OnDestroy()
    {
        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Stop();
        }
        isCameraInitialized = false;
        Debug.Log("카메라 정지 및 리소스 해제됨.");
    }
}
