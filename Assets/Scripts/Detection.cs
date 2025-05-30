using UnityEngine;

// 탐지된 객체의 정보를 담는 구조체
public struct Detection
{
    public string Label;
    public float Score;
    public Rect BoundingBox; // 모델 입력 기준 좌표 (예: 640x640) 및 크기

    public override string ToString()
    {
        return $"{Label} ({Score:P2}) at {BoundingBox}";
    }
} 